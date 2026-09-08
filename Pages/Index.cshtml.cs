using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NettoMarkdowns.Models;
using NettoMarkdowns.Services;

namespace NettoMarkdowns.Pages;

public class IndexModel(
    SallingClient client,
    IOptions<SallingOptions> options,
    ApiQuota quota,
    ILogger<IndexModel> logger) : PageModel
{
    /// <summary>Remembers the picked stores between visits, so the choice survives a restart.</summary>
    private const string StoreCookieName = "netto.stores";

    private static readonly char[] ListSeparators = [',', ';', ' '];

    private readonly SallingOptions _options = options.Value;

    // --- Filters, bound from the query string so every view is a shareable URL ---

    [BindProperty(SupportsGet = true)]
    public string? Zips { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true)]
    public int MinDiscount { get; set; }

    [BindProperty(SupportsGet = true)]
    public decimal? MaxPrice { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "percent";

    [BindProperty(SupportsGet = true)]
    public bool Flat { get; set; }

    /// <summary>
    /// Store ids to show — one value per ticked checkbox. This has to be an array:
    /// binding repeated values onto a plain string keeps only the first one, which
    /// silently reduces any multi-store selection to a single store.
    /// Empty means "every store in the postcodes".
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string[] StoreIds { get; set; } = [];

    /// <summary>
    /// Hidden marker on the filter form. It tells us the user actually operated the
    /// picker, so an empty <see cref="StoreIds"/> means "they unchecked everything"
    /// rather than "they said nothing" — only then do we overwrite the saved choice.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public bool StoresSubmitted { get; set; }

    /// <summary>
    /// Product categories to keep, one value per ticked box (same array reasoning as
    /// <see cref="StoreIds"/>). Empty means every category. Not remembered between
    /// visits — unlike your stores, what you're shopping for changes every time.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string[] Categories { get; set; } = [];

    // --- Results ---

    public List<StoreGroup> Groups { get; private set; } = [];
    public List<MarkdownItem> AllItems { get; private set; } = [];
    public List<StoreOption> AvailableStores { get; private set; } = [];
    public List<CategoryOption> AvailableCategories { get; private set; } = [];
    public List<CategoryGroupOption> AvailableCategoryGroups { get; private set; } = [];
    public string? Error { get; private set; }
    public bool NeedsApiKey => !client.HasApiKey;
    public DateTimeOffset FetchedAt { get; private set; }

    public int ItemCount => AllItems.Count;
    public int StoreCount => Groups.Count;
    public double BestDiscount => AllItems.Count == 0 ? 0 : AllItems.Max(i => i.PercentDiscount);

    public int SelectedStoreCount => AvailableStores.Count(s => s.Selected);
    public bool IsStoreFiltered => SelectedStoreCount > 0;
    public int SelectedCategoryCount => AvailableCategories.Count(c => c.Selected);
    public bool IsCategoryFiltered => SelectedCategoryCount > 0;

    public string EffectiveZips => string.IsNullOrWhiteSpace(Zips)
        ? string.Join(", ", _options.DefaultZips)
        : Zips;

    /// <summary>Current selection, re-emitted as hidden fields by the Refresh button.</summary>
    public IEnumerable<string> SelectedStoreIds =>
        AvailableStores.Where(s => s.Selected).Select(s => s.Id);

    public IEnumerable<string> SelectedCategories =>
        AvailableCategories.Where(c => c.Selected).Select(c => c.Value);

    public int LookupsUsed => quota.Used;
    public int LookupsLimit => _options.ProductsDailyLimit;

    /// <summary>
    /// Per-row price-per-kilo lookup, reached at ?handler=UnitPrice&amp;ean=…
    /// It's a separate handler rather than part of the page so pressing one button
    /// costs one API call, not a whole page of them.
    /// </summary>
    public async Task<IActionResult> OnGetUnitPriceAsync(
        string ean, string storeId, decimal price, CancellationToken cancellationToken)
    {
        var result = await client.GetProductAsync(ean, storeId, price, quota, cancellationToken);

        return new JsonResult(new
        {
            ok = result.Ok,
            unitPrice = result.UnitPrice,
            detail = result.Detail,
            error = result.Error,
            used = quota.Used,
            limit = _options.ProductsDailyLimit
        });
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (NeedsApiKey) return;

        var zips = Split(EffectiveZips);
        if (zips.Length == 0)
        {
            Error = "Enter at least one postcode.";
            return;
        }

        var selectedStores = ResolveStoreSelection();

        try
        {
            var stores = await client.GetByZipsAsync(zips, cancellationToken);
            FetchedAt = DateTimeOffset.Now;

            var brands = _options.Brands;
            var inScope = stores
                .Where(s => brands.Length == 0 ||
                            brands.Contains(s.Store.Brand, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s.Store.DisplayName, StringComparer.CurrentCulture)
                .ToList();

            // The picker lists every store in the postcodes, including ones currently
            // filtered out — otherwise you could never select them again.
            AvailableStores = inScope
                .Select(s => new StoreOption(
                    s.Store.Id,
                    s.Store.DisplayName,
                    s.Store.Address?.ToString(),
                    s.Clearances.Count,
                    selectedStores.Contains(s.Store.Id)))
                .ToList();

            var shown = selectedStores.Count == 0
                ? inScope
                : inScope.Where(s => selectedStores.Contains(s.Store.Id)).ToList();

            // Everything that passes every filter *except* the category one. The
            // category list is built from these, so its counts always match what
            // you'd actually get by ticking a box, and no dead options are offered.
            var candidates = shown
                .Select(s => (s.Store, Items: s.Clearances
                    .Select(c => new MarkdownItem(s.Store, c.Product, c.Offer))
                    .Where(MatchesFilters)
                    .ToList()))
                .ToList();

            // Deliberately NOT run through Split: category labels legitimately contain
            // spaces, commas and ampersands ("Frugt & grønt", "Kød, fisk & fjerkræ").
            // Each checkbox already posts one complete value, so splitting would shred
            // them into fragments that match no product at all.
            var selectedCategories = Categories
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

            AvailableCategoryGroups = candidates
                .SelectMany(c => c.Items)
                .GroupBy(i => i.CategoryKey, StringComparer.CurrentCultureIgnoreCase)
                .Select(g =>
                {
                    var (group, leaf) = CategoryTree.Resolve(g.Key, _options.CategoryGroups);
                    return new
                    {
                        Group = group,
                        Option = new CategoryOption(g.Key, leaf, g.Count(), selectedCategories.Contains(g.Key))
                    };
                })
                .GroupBy(x => x.Group, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new CategoryGroupOption(
                    g.Key,
                    g.Select(x => x.Option)
                        .OrderByDescending(o => o.Count)
                        .ThenBy(o => o.Label, StringComparer.CurrentCulture)
                        .ToList()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.CurrentCulture)
                .ToList();

            AvailableCategories = AvailableCategoryGroups.SelectMany(g => g.Children).ToList();

            Groups = candidates
                .Select(c => new StoreGroup(
                    c.Store,
                    Sorted(c.Items.Where(i => MatchesCategory(i, selectedCategories)))))
                .Where(g => g.Items.Count > 0)
                .ToList();

            AllItems = Sorted(Groups.SelectMany(g => g.Items));
        }
        catch (SallingException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Browser navigated away mid-request; nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure while loading markdowns");
            Error = "Something went wrong loading the markdowns. Check the console log for details.";
        }
    }

    /// <summary>
    /// Works out which stores to show, in priority order: what the picker just
    /// submitted, then what the URL asks for, then what we saved last time.
    /// </summary>
    private HashSet<string> ResolveStoreSelection()
    {
        // Accepts both shapes: ?StoreIds=a&StoreIds=b from the checkboxes, and
        // ?StoreIds=a,b if you'd rather hand-write the URL.
        var requested = StoreIds
            .SelectMany(Split)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (StoresSubmitted)
        {
            SaveStoreSelection(requested);
            return requested;
        }

        return requested.Count > 0
            ? requested
            : Split(Request.Cookies[StoreCookieName]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveStoreSelection(IReadOnlyCollection<string> storeIds)
    {
        if (storeIds.Count == 0)
        {
            Response.Cookies.Delete(StoreCookieName);
            return;
        }

        Response.Cookies.Append(StoreCookieName, string.Join(',', storeIds), new CookieOptions
        {
            Expires = DateTimeOffset.Now.AddDays(180),
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps
        });
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(ListSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesCategory(MarkdownItem item, HashSet<string> selected) =>
        selected.Count == 0 || selected.Contains(item.CategoryKey);

    private bool MatchesFilters(MarkdownItem item)
    {
        if (item.PercentDiscount < MinDiscount) return false;
        if (MaxPrice is { } max && item.Offer.NewPrice > max) return false;

        if (!string.IsNullOrWhiteSpace(Query))
        {
            var haystack = $"{item.Product.Description} {item.Product.Category}";
            if (!haystack.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private List<MarkdownItem> Sorted(IEnumerable<MarkdownItem> items) => Sort switch
    {
        "saving" => items.OrderByDescending(i => i.Saving).ToList(),
        "price" => items.OrderBy(i => i.Offer.NewPrice).ToList(),
        "expiry" => items.OrderBy(i => i.Offer.EndTime ?? DateTimeOffset.MaxValue).ToList(),
        "name" => items.OrderBy(i => i.Product.Description, StringComparer.CurrentCulture).ToList(),
        "stock" => items.OrderByDescending(i => i.Offer.Stock).ToList(),
        _ => items.OrderByDescending(i => i.PercentDiscount).ToList()
    };

    public sealed record StoreOption(string Id, string Name, string? Address, int ItemCount, bool Selected);

    /// <param name="Value">The raw category as the API reports it — what actually gets filtered on.</param>
    /// <param name="Label">The leaf name shown in the tree, without its group prefix.</param>
    public sealed record CategoryOption(string Value, string Label, int Count, bool Selected);

    public sealed record CategoryGroupOption(string Name, List<CategoryOption> Children)
    {
        public int Count => Children.Sum(c => c.Count);
        public bool AllSelected => Children.Count > 0 && Children.All(c => c.Selected);
        public bool AnySelected => Children.Any(c => c.Selected);

        /// <summary>A group with one child is just a category — render it without an expander.</summary>
        public bool IsLeafOnly => Children.Count == 1;

        /// <summary>
        /// Label for that single-child case. Usually the group and the category are the
        /// same word, but when the API handed us a real path ("Frost &gt; Is") both parts
        /// are worth showing, or the item looks like it belongs to no group at all.
        /// </summary>
        public string LeafOnlyLabel =>
            Children.Count == 1 &&
            !string.Equals(Name, Children[0].Label, StringComparison.CurrentCultureIgnoreCase)
                ? $"{Name} › {Children[0].Label}"
                : Children[0].Label;
    }

    public sealed record StoreGroup(Store Store, List<MarkdownItem> Items)
    {
        public double BestDiscount => Items.Count == 0 ? 0 : Items.Max(i => i.PercentDiscount);
    }

    public sealed record MarkdownItem(Store Store, Product Product, Offer Offer)
    {
        /// <summary>
        /// Dates are formatted in the interface's language rather than the server's
        /// culture, so "Today, 8 Sep" doesn't sit next to a Danish "tors. 10 sep.".
        /// Prices deliberately still follow the machine's culture — 12,00 kr. is right.
        /// </summary>
        private static readonly CultureInfo DateCulture = CultureInfo.GetCultureInfo("en-GB");

        /// <summary>Not every product carries a category; those still need a chip of
        /// their own, otherwise filtering would silently drop them.</summary>
        public const string NoCategory = "Uncategorised";

        public decimal Saving => Offer.OriginalPrice - Offer.NewPrice;
        public double PercentDiscount => Offer.PercentDiscount;

        public string CategoryKey =>
            string.IsNullOrWhiteSpace(Product.Category) ? NoCategory : Product.Category!;

        /// <summary>
        /// Loose goods sold over the counter. For these the API's prices are already
        /// per kilo — the shelf price of a whole chicken is not 59,95 kr.
        /// </summary>
        public bool PricedByWeight =>
            string.Equals(Offer.StockUnit, "kg", StringComparison.OrdinalIgnoreCase);

        public string StockLabel => Offer.StockUnit switch
        {
            // `stock` on weighed items sits at ~0.19 for every product regardless of
            // what it is, so it isn't a quantity and is not worth showing as one.
            "kg" => "by weight",
            null or "" or "each" => $"{Offer.Stock:0.##} pcs",
            var unit => $"{Offer.Stock:0.##} {unit}"
        };

        /// <summary>
        /// Price per kilo (or litre) where it can be established honestly: directly,
        /// for goods the API already prices by weight, otherwise from a pack size in
        /// the description. Null when neither applies — which is most items.
        /// </summary>
        public string? UnitPriceLabel
        {
            get
            {
                if (PricedByWeight) return $"{Offer.NewPrice:0.00} kr./kg";

                if (PackSizeParser.Parse(Product.Description) is not { } size) return null;
                if (size.Amount <= 0) return null;

                var perUnit = Offer.NewPrice / size.Amount;
                return $"{perUnit:0.00} kr./{size.Suffix}";
            }
        }

        /// <summary>
        /// The day the markdown stops. Every offer the API returns ends at 23:59:59
        /// local time, never part-way through a day — so this is a date, and for
        /// short-dated goods it is the product's last sale date.
        /// </summary>
        public DateOnly? LastSaleDate =>
            Offer.EndTime is { } end ? DateOnly.FromDateTime(end.ToLocalTime().Date) : null;

        private int? DaysLeft =>
            LastSaleDate is { } date ? date.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber : null;

        public bool EndsToday => DaysLeft == 0;
        public bool Expired => DaysLeft < 0;

        /// <summary>Weekday and date together, e.g. "Today, 8 Sep" or "Thu 10 Sep".</summary>
        public string? EndsLabel
        {
            get
            {
                if (LastSaleDate is not { } date) return null;

                var label = DaysLeft switch
                {
                    < 0 => $"Expired {date.ToString("d MMM", DateCulture)}",
                    0 => $"Today, {date.ToString("d MMM", DateCulture)}",
                    1 => $"Tomorrow, {date.ToString("d MMM", DateCulture)}",
                    _ => date.ToString("ddd d MMM", DateCulture)
                };

                // Defensive: no offer has ever come back part-way through a day, but if
                // one does, the time matters and shouldn't be silently dropped.
                var local = Offer.EndTime!.Value.ToLocalTime();
                if (local.TimeOfDay < new TimeSpan(23, 55, 0)) label += $" · {local:HH:mm}";

                return label;
            }
        }

        public string EndsTitle => LastSaleDate is { } date
            ? $"Markdown runs to the end of {date.ToString("dddd d MMMM yyyy", DateCulture)}"
              + " — normally the product's last sale date"
            : "";
    }
}
