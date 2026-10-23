using Microsoft.Extensions.Options;
using NettoMarkdowns.Models;

namespace NettoMarkdowns.Services;

/// <summary>
/// Quietly fills <see cref="ProductStore"/> with pack sizes for products currently on
/// markdown, a few per poll, so most rows already show kr./kg when you open the page.
/// It never goes past <see cref="SallingOptions.ProductsBackgroundLimit"/> calls a
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
        var fetched = 0;

        foreach (var (storeId, clearance) in wanted)
        {
            if (fetched >= perPoll) break;
            if (known.ContainsKey(clearance.Product.Ean!)) continue;

            var load = await client.LoadProductAsync(clearance.Product.Ean!, storeId, quota, limit, ct);
            if (load.Stop)
            {
                logger.LogInformation("Product weight filler stopping: {Reason}", load.Error);
                return;
            }

            fetched++;
        }

        if (fetched > 0)
        {
            logger.LogInformation("Looked up {Count} product weights, {Used} calls used today",
                fetched, quota.Used);
        }
    }
}
