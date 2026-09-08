using System.Globalization;
using System.Text.RegularExpressions;

namespace NettoMarkdowns.Services;

/// <summary>A pack size normalised to kilograms or litres.</summary>
public readonly record struct PackSize(decimal Amount, PackUnit Unit)
{
    public string Suffix => Unit == PackUnit.Kilogram ? "kg" : "l";
}

public enum PackUnit
{
    Kilogram,
    Litre
}

/// <summary>
/// Pulls a pack size out of a product description, e.g. "TYKMÆLK 1KG KLØVER" or
/// "TRIO DIP 210G LA CAMPAGNA".
///
/// Worth knowing before relying on this: Netto's descriptions are till-system names,
/// and only about 5% of them carry a size at all — most read like
/// "HK GRIS 3-7% MESTERHAKKET". So this fills in a minority of rows by design, and
/// returning null is the normal case rather than a failure.
/// </summary>
public static partial class PackSizeParser
{
    [GeneratedRegex(
        @"(?:(?<mult>\d{1,2})\s*[x×]\s*)?(?<amount>\d{1,4}(?:[.,]\d{1,3})?)\s*(?<unit>kg|g|gr|ml|cl|dl|l)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    public static PackSize? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        foreach (Match match in SizePattern().Matches(description))
        {
            if (!decimal.TryParse(
                    match.Groups["amount"].Value.Replace(',', '.'),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
                amount <= 0)
            {
                continue;
            }

            var multiplier = 1m;
            if (match.Groups["mult"].Success &&
                decimal.TryParse(match.Groups["mult"].Value, out var parsedMultiplier) &&
                parsedMultiplier > 0)
            {
                multiplier = parsedMultiplier;
            }

            var total = amount * multiplier;

            return match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "kg" => new PackSize(total, PackUnit.Kilogram),
                "g" or "gr" => new PackSize(total / 1000m, PackUnit.Kilogram),
                "l" => new PackSize(total, PackUnit.Litre),
                "dl" => new PackSize(total / 10m, PackUnit.Litre),
                "cl" => new PackSize(total / 100m, PackUnit.Litre),
                "ml" => new PackSize(total / 1000m, PackUnit.Litre),
                _ => null
            };
        }

        return null;
    }
}
