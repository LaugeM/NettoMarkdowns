namespace NettoMarkdowns.Models;

/// <summary>
/// One entry from GET /v1/food-waste/ — a single store plus everything it has
/// currently marked down ("Mad med mening" / anti-food-waste clearances).
/// Property names are matched case-insensitively against the API's camelCase JSON,
/// so no [JsonPropertyName] attributes are needed.
/// </summary>
public sealed class StoreClearances
{
    public Store Store { get; set; } = new();
    public List<Clearance> Clearances { get; set; } = [];
}

public sealed class Store
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "";
    public Address? Address { get; set; }
    public List<StoreHours> Hours { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Closing time for today, as "HH:mm", or null if unknown/closed.</summary>
    public string? ClosesToday
    {
        get
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var entry = Hours.FirstOrDefault(h =>
                h.Date is not null && h.Date.StartsWith(today, StringComparison.Ordinal) &&
                string.Equals(h.Type, "Store", StringComparison.OrdinalIgnoreCase));

            entry ??= Hours.FirstOrDefault(h => h.Date is not null && h.Date.StartsWith(today, StringComparison.Ordinal));

            if (entry is null || entry.Closed || entry.Close is null) return null;
            return DateTimeOffset.TryParse(entry.Close, out var close)
                ? close.ToLocalTime().ToString("HH:mm")
                : null;
        }
    }
}

public sealed class Address
{
    public string? Street { get; set; }
    public string? Zip { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }

    public override string ToString() => string.Join(", ",
        new[] { Street, string.Join(' ', new[] { Zip, City }.Where(s => !string.IsNullOrWhiteSpace(s))) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class StoreHours
{
    public string? Date { get; set; }
    public string? Type { get; set; }
    public string? Open { get; set; }
    public string? Close { get; set; }
    public bool Closed { get; set; }
}

public sealed class Clearance
{
    public Offer Offer { get; set; } = new();
    public Product Product { get; set; } = new();
}

public sealed class Offer
{
    public string? Currency { get; set; }
    public string? Ean { get; set; }
    public decimal OriginalPrice { get; set; }
    public decimal NewPrice { get; set; }
    public decimal Discount { get; set; }
    public double PercentDiscount { get; set; }
    public double Stock { get; set; }
    public string? StockUnit { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }
}

public sealed class Product
{
    public string? Description { get; set; }
    public string? Ean { get; set; }
    public string? Image { get; set; }
    public Dictionary<string, string>? Categories { get; set; }

    public string? Category =>
        Categories is null ? null :
        Categories.TryGetValue("da", out var da) ? da :
        Categories.TryGetValue("en", out var en) ? en : null;
}
