namespace NettoMarkdowns.Services;

public sealed class SallingOptions
{
    public const string SectionName = "Salling";

    /// <summary>
    /// Bearer token from https://developer.sallinggroup.com (My apps).
    /// Keep it out of source control — set it with:
    ///   dotnet user-secrets set "Salling:ApiKey" "your-key"
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Postcodes searched when the user hasn't typed any.</summary>
    public string[] DefaultZips { get; set; } = [];

    /// <summary>Only show these brands (empty = all Salling brands: netto, bilka, foetex).</summary>
    public string[] Brands { get; set; } = ["netto"];

    /// <summary>
    /// Groups the flat category labels the API returns into a two-level tree.
    /// Key is the group shown in the filter, value is the categories that belong to
    /// it (matched case-insensitively). Anything not listed becomes its own
    /// top-level entry, so an incomplete map never hides products — extend it as you
    /// see what your stores actually return.
    /// </summary>
    public Dictionary<string, string[]> CategoryGroups { get; set; } = [];

    /// <summary>
    /// Daily call budget for the Products EAN API. Salling caps it at 100 requests a
    /// day, which is why per-kilo lookups are a button rather than something the page
    /// does for every row.
    /// </summary>
    public int ProductsDailyLimit { get; set; } = 100;

    /// <summary>How long a product lookup is reused. Pack weights don't change.</summary>
    public TimeSpan ProductCacheDuration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Serve built-in demo data instead of calling the API (no key needed).</summary>
    public bool UseSampleData { get; set; }

    /// <summary>How long a response is reused before we call the API again.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(60);
}
