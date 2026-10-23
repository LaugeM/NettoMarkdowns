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
    ProductStore products,
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

    /// <summary>The outcome of asking for one product's pack size.</summary>
    /// <param name="Variant">The product, when known.</param>
    /// <param name="Error">Why there is no <paramref name="Variant"/>, in words fit for the page.</param>
    /// <param name="Stop">True when asking for more would be pointless right now (quota spent, key refused).</param>
    public sealed record ProductLoad(ProductVariant? Variant, string? Error = null, bool Stop = false);

    /// <summary>
    /// Looks one product up by barcode to establish a price per kilo. Answers come from
    /// the local <see cref="ProductStore"/> when possible; only an unknown barcode costs
    /// one of the 100 daily calls, and the answer is kept for good afterwards.
    /// The endpoint is per store, so a store id is required for the call itself, but the
    /// pack size is the same everywhere and is saved by barcode alone.
    /// </summary>
    public async Task<ProductLookup> GetProductAsync(
        string ean, string storeId, decimal currentPrice, ApiQuota quota, CancellationToken ct)
    {
        var load = await LoadProductAsync(ean, storeId, quota, _options.ProductsDailyLimit, ct);
        return load.Variant is null
            ? ProductLookup.Failed(load.Error ?? "Salling has no record for this barcode.")
            : ProductLookup.From(new ProductResponse { Instore = load.Variant }, currentPrice);
    }

    /// <summary>
    /// Returns what is known about a barcode, calling the API only if the store has
    /// nothing. <paramref name="limit"/> is how many calls the caller may take the day
    /// to, so the background filler can leave part of the budget for the button.
    /// </summary>
    public async Task<ProductLoad> LoadProductAsync(
        string ean, string storeId, ApiQuota quota, int limit, CancellationToken ct)
    {
        if (!HasApiKey) return new ProductLoad(null, "No Salling API key configured.", Stop: true);
        if (string.IsNullOrWhiteSpace(ean)) return new ProductLoad(null, "This item has no barcode.");
        if (string.IsNullOrWhiteSpace(storeId)) return new ProductLoad(null, "This item has no store.");

        if (products.Get(ean) is { } known)
        {
            return known.Variant is null
                ? new ProductLoad(null, "Salling has no product with that barcode.")
                : new ProductLoad(known.Variant);
        }

        if (!quota.TryConsume(limit))
        {
            return new ProductLoad(null,
                $"Daily limit of {limit} product lookups reached. Resets tomorrow.", Stop: true);
        }

        var path = $"v2/products/{Uri.EscapeDataString(ean)}?storeId={Uri.EscapeDataString(storeId)}";

        try
        {
            using var response = await http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The call was made, so it stays counted; remember the miss to avoid repeating it.
                products.Save(ean, null);
                return new ProductLoad(null, "Salling has no product with that barcode.");
            }

            if (!response.IsSuccessStatusCode)
            {
                quota.Refund();
                logger.LogWarning("Products API {Status} for {Ean}: {Body}",
                    (int)response.StatusCode, ean, body);

                return response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => new ProductLoad(null,
                        "Your API key can't reach the Products EAN API. Add that API to your "
                        + "application on developer.sallinggroup.dev.", Stop: true),
                    HttpStatusCode.TooManyRequests => new ProductLoad(null,
                        "Salling says the daily quota is spent.", Stop: true),
                    _ => new ProductLoad(null, $"Products API returned {(int)response.StatusCode}.")
                };
            }

            var variant = JsonSerializer.Deserialize<ProductResponse>(body, JsonOptions)?.Best;
            products.Save(ean, variant);
            return variant is null
                ? new ProductLoad(null, "Salling has no record for this barcode.")
                : new ProductLoad(variant);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            quota.Refund();
            logger.LogError(ex, "Product lookup failed for {Ean}", ean);
            return new ProductLoad(null, "Could not reach the Products API.");
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
