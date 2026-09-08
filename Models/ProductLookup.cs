namespace NettoMarkdowns.Models;

/// <summary>
/// Response of GET /v2/products/{ean}?storeId=… — the same product as sold in the
/// physical store and in the webshop. Either half can be null.
/// </summary>
public sealed class ProductResponse
{
    public ProductVariant? Instore { get; set; }
    public ProductVariant? Webshop { get; set; }

    /// <summary>The in-store record is the one whose price matches a shelf markdown.</summary>
    public ProductVariant? Best => Instore ?? Webshop;
}

public sealed class ProductVariant
{
    public string? Ean { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Pack size, in <see cref="ContentsUnit"/> — e.g. 250 with "g".</summary>
    public decimal? Contents { get; set; }
    public string? ContentsUnit { get; set; }

    /// <summary>Normal shelf price, before any markdown.</summary>
    public decimal? Price { get; set; }

    /// <summary>Unit <see cref="UnitPrice"/> is expressed in, e.g. "kg".</summary>
    public string? Unit { get; set; }

    /// <summary>Normal price per <see cref="Unit"/>, again before any markdown.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>Pack size converted to kilos or litres, when it can be.</summary>
    public decimal? NormalisedContents => (Contents, ContentsUnit?.Trim().ToLowerInvariant()) switch
    {
        (null or <= 0, _) => null,
        (var c, "kg" or "l") => c,
        (var c, "g" or "ml") => c / 1000m,
        (var c, "cl") => c / 100m,
        (var c, "dl") => c / 10m,
        _ => null   // "stk" and friends: a count, not a weight
    };

    public string Suffix => ContentsUnit?.Trim().ToLowerInvariant() is "l" or "ml" or "cl" or "dl"
        ? "l"
        : "kg";
}

/// <summary>What the page shows for one price-per-kilo lookup.</summary>
public sealed record ProductLookup(
    bool Ok,
    string? UnitPrice = null,
    string? Detail = null,
    string? Error = null)
{
    public static ProductLookup Failed(string error) => new(false, Error: error);

    /// <summary>
    /// Turns the API's record into a price per kilo for the *marked-down* price. The
    /// API's own unitPrice is the normal one, so it can't be shown as-is — but it is
    /// worth reporting alongside, since the gap is the whole point of the exercise.
    /// </summary>
    public static ProductLookup From(ProductResponse? response, decimal currentPrice)
    {
        var variant = response?.Best;
        if (variant is null) return Failed("Salling has no record for this barcode.");

        // Preferred: an explicit pack size.
        var contents = variant.NormalisedContents;

        // Fallback: derive the pack size from normal price ÷ normal price per kilo.
        if (contents is null &&
            variant is { Price: > 0, UnitPrice: > 0 })
        {
            contents = variant.Price / variant.UnitPrice;
        }

        if (contents is not > 0)
        {
            return Failed(variant.Contents is > 0
                ? $"Sold by count ({variant.Contents:0.##} {variant.ContentsUnit}), so it has no price per kilo."
                : "Salling lists no pack size for this product.");
        }

        var perKilo = currentPrice / contents.Value;

        var detail = variant.Contents is > 0
            ? $"{variant.Contents:0.##} {variant.ContentsUnit}"
            : $"{contents.Value:0.###} {variant.Suffix}";

        if (variant.UnitPrice is > 0)
        {
            detail += $" · normally {variant.UnitPrice:0.00} kr./{variant.Unit ?? variant.Suffix}";
        }

        return new ProductLookup(true, $"{perKilo:0.00} kr./{variant.Suffix}", detail);
    }
}
