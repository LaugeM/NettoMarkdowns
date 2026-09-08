using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NettoMarkdowns.Models;

namespace NettoMarkdowns.Services;

/// <summary>
/// Typed HttpClient over Salling Group's public Food Waste API.
/// Registered in Program.cs with AddHttpClient&lt;SallingClient&gt;, which supplies
/// the HttpClient (base address + Authorization header) via DI.
/// </summary>
public sealed class SallingClient(
    HttpClient http,
    IMemoryCache cache,
    IOptions<SallingOptions> options,
    ILogger<SallingClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SallingOptions _options = options.Value;

    public bool HasApiKey => _options.UseSampleData || !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <summary>All stores in a postcode, with what they currently have marked down.</summary>
    public Task<IReadOnlyList<StoreClearances>> GetByZipAsync(string zip, CancellationToken ct) =>
        GetAsync($"v1/food-waste/?zip={Uri.EscapeDataString(zip)}", ct);

    /// <summary>Stores within <paramref name="radiusMetres"/> of a coordinate.</summary>
    public Task<IReadOnlyList<StoreClearances>> GetNearbyAsync(
        double latitude, double longitude, int radiusMetres, CancellationToken ct) =>
        GetAsync(
            FormattableString.Invariant($"v1/food-waste/?geo={latitude},{longitude}&radius={radiusMetres}"),
            ct);

    /// <summary>Runs several lookups concurrently and merges them, de-duplicating stores by id.</summary>
    public async Task<IReadOnlyList<StoreClearances>> GetByZipsAsync(
        IEnumerable<string> zips, CancellationToken ct)
    {
        var distinct = zips
            .Select(z => z.Trim())
            .Where(z => z.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length == 0) return [];

        var results = await Task.WhenAll(distinct.Select(z => GetByZipAsync(z, ct)));

        return results
            .SelectMany(r => r)
            .GroupBy(r => r.Store.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Looks one product up by barcode to establish a price per kilo. The endpoint is
    /// per store — prices differ between them — so the store id is required.
    /// Deliberately not called for whole pages: the quota is 100 a day, so this runs
    /// only when you press the button on a row, and results are cached for a week.
    /// </summary>
    public async Task<ProductLookup> GetProductAsync(
        string ean, string storeId, decimal currentPrice, ApiQuota quota, CancellationToken ct)
    {
        if (!HasApiKey) return ProductLookup.Failed("No Salling API key configured.");
        if (string.IsNullOrWhiteSpace(ean)) return ProductLookup.Failed("This item has no barcode.");
        if (string.IsNullOrWhiteSpace(storeId)) return ProductLookup.Failed("This item has no store.");

        var cacheKey = $"product:{storeId}:{ean}";
        if (cache.TryGetValue(cacheKey, out ProductResponse? cached) && cached is not null)
        {
            return ProductLookup.From(cached, currentPrice);
        }

        if (!quota.TryConsume(_options.ProductsDailyLimit))
        {
            return ProductLookup.Failed(
                $"Daily limit of {_options.ProductsDailyLimit} product lookups reached. Resets tomorrow.");
        }

        var path = $"v2/products/{Uri.EscapeDataString(ean)}?storeId={Uri.EscapeDataString(storeId)}";

        try
        {
            using var response = await http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                quota.Refund();
                logger.LogWarning("Products API {Status} for {Ean}: {Body}",
                    (int)response.StatusCode, ean, body);

                return ProductLookup.Failed(response.StatusCode switch
                {
                    HttpStatusCode.Forbidden =>
                        "Your API key can't reach the Products EAN API. Add that API to your "
                        + "application on developer.sallinggroup.dev.",
                    HttpStatusCode.NotFound => "Salling has no product with that barcode.",
                    HttpStatusCode.TooManyRequests => "Salling says the daily quota is spent.",
                    _ => $"Products API returned {(int)response.StatusCode}."
                });
            }

            var product = JsonSerializer.Deserialize<ProductResponse>(body, JsonOptions);
            cache.Set(cacheKey, product, _options.ProductCacheDuration);
            return ProductLookup.From(product, currentPrice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            quota.Refund();
            logger.LogError(ex, "Product lookup failed for {Ean}", ean);
            return ProductLookup.Failed("Could not reach the Products API.");
        }
    }

    private async Task<IReadOnlyList<StoreClearances>> GetAsync(string path, CancellationToken ct)
    {
        if (!HasApiKey)
        {
            throw new SallingException(
                "No Salling API key configured. Get a free one at developer.sallinggroup.com, " +
                "then run: dotnet user-secrets set \"Salling:ApiKey\" \"<your key>\"");
        }

        if (cache.TryGetValue(path, out IReadOnlyList<StoreClearances>? cached) && cached is not null)
        {
            logger.LogDebug("Cache hit for {Path}", path);
            return cached;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(path, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SallingException($"Could not reach api.sallinggroup.com: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Salling API {Status} for {Path}: {Body}",
                    (int)response.StatusCode, path, body);

                throw new SallingException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        "Salling rejected the API key. Check that it is correct and that the " +
                        "\"Food Waste\" API is enabled for your app on developer.sallinggroup.com.",
                    HttpStatusCode.TooManyRequests =>
                        "Rate limited by Salling. Wait a moment before refreshing.",
                    HttpStatusCode.NotFound =>
                        $"Salling returned 404 for {path} — no store matched that search.",
                    _ => $"Salling API returned {(int)response.StatusCode} {response.ReasonPhrase}."
                });
            }

            var parsed = Parse(body);
            cache.Set(path, parsed, _options.CacheDuration);
            return parsed;
        }
    }

    /// <summary>
    /// The endpoint returns an array of stores for a zip/geo search, but a bare object
    /// when it resolves to exactly one store — handle both.
    /// </summary>
    private static IReadOnlyList<StoreClearances> Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array =>
                    document.RootElement.Deserialize<List<StoreClearances>>(JsonOptions) ?? [],
                JsonValueKind.Object =>
                    document.RootElement.Deserialize<StoreClearances>(JsonOptions) is { } single
                        ? [single]
                        : [],
                _ => []
            };
        }
        catch (JsonException ex)
        {
            throw new SallingException(
                "Salling returned something that isn't the expected JSON shape. " +
                "The API may have changed. " + ex.Message, ex);
        }
    }
}
