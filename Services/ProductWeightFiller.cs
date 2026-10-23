using Microsoft.Extensions.Options;
using NettoMarkdowns.Models;

namespace NettoMarkdowns.Services;

/// <summary>
/// Quietly fills <see cref="ProductStore"/> with pack sizes for products currently on
/// markdown, a few per poll, so most rows already show kr./kg when you open the page.
/// Once there is nothing new to look up, spare calls recheck weights older than
/// <see cref="SallingOptions.ProductsRefreshAfter"/>. It never goes past <see cref="SallingOptions.ProductsBackgroundLimit"/> calls a
/// day, which leaves the rest for the per-row button.
/// </summary>
public sealed class ProductWeightFiller(
    SallingClient client,
    ProductStore products,
    ApiQuota quota,
    IOptions<SallingOptions> options,
    ILogger<ProductWeightFiller> logger)
{
    private readonly SallingOptions _options = options.Value;

    public async Task FillAsync(IReadOnlyList<StoreClearances> stores, CancellationToken ct)
    {
        var perPoll = _options.ProductsPerPoll;
        var limit = Math.Min(_options.ProductsBackgroundLimit, _options.ProductsDailyLimit);
        if (perPoll <= 0 || limit <= 0 || quota.Used >= limit) return;

        // Items already priced per kilo, or with a size in the name, need no lookup.
        // Deepest discounts first: those are the ones worth comparing.
        var wanted = stores
            .SelectMany(s => s.Clearances.Select(c => (StoreId: s.Store.Id, Clearance: c)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Clearance.Product.Ean) &&
                        !string.Equals(x.Clearance.Offer.StockUnit, "kg", StringComparison.OrdinalIgnoreCase) &&
                        PackSizeParser.Parse(x.Clearance.Product.Description) is null)
            .OrderByDescending(x => x.Clearance.Offer.PercentDiscount)
            .DistinctBy(x => x.Clearance.Product.Ean)
            .ToList();

        if (wanted.Count == 0) return;

        var known = products.GetMany(wanted.Select(x => x.Clearance.Product.Ean!));

        // New barcodes come first. Calls left over this poll go to rechecking the oldest
        // saved weights, in case Salling corrected one or a supplier reused a barcode.
        var missing = wanted.Where(x => !known.ContainsKey(x.Clearance.Product.Ean!));
        var staleBefore = DateTimeOffset.UtcNow - _options.ProductsRefreshAfter;
        var stale = _options.ProductsRefreshAfter > TimeSpan.Zero
            ? wanted
                .Where(x => known.TryGetValue(x.Clearance.Product.Ean!, out var e) &&
                            e.Variant is not null && e.FetchedAt < staleBefore)
                .OrderBy(x => known[x.Clearance.Product.Ean!].FetchedAt)
            : Enumerable.Empty<(string StoreId, Clearance Clearance)>();

        var fetched = 0;
        var rechecked = 0;

        foreach (var (storeId, clearance) in missing.Concat(stale))
        {
            if (fetched + rechecked >= perPoll) break;

            var ean = clearance.Product.Ean!;
            var old = known.TryGetValue(ean, out var entry) ? entry.Variant : null;

            var load = await client.LoadProductAsync(ean, storeId, quota, limit, ct, refresh: old is not null);
            if (load.Stop)
            {
                logger.LogInformation("Product weight filler stopping: {Reason}", load.Error);
                break;
            }

            if (old is null)
            {
                fetched++;
                continue;
            }

            rechecked++;
            if (load.Variant is { } now &&
                (now.Contents != old.Contents ||
                 !string.Equals(now.ContentsUnit, old.ContentsUnit, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Pack size changed for {Ean} ({Name}): {OldContents} {OldUnit} is now {NewContents} {NewUnit}",
                    ean, clearance.Product.Description, old.Contents, old.ContentsUnit, now.Contents, now.ContentsUnit);
            }
        }

        if (fetched + rechecked > 0)
        {
            logger.LogInformation("Looked up {Count} new and rechecked {Rechecked} product weights, {Used} calls used today",
                fetched, rechecked, quota.Used);
        }
    }
}
