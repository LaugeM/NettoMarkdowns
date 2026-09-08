namespace NettoMarkdowns.Services;

/// <summary>
/// Turns a product's category label into a two-level path, so the filter can be a
/// tree ("Kød &amp; fisk" &gt; "Kød") instead of one long flat list.
/// </summary>
public static class CategoryTree
{
    /// <summary>Separators the API might use if it ever hands us a path directly.</summary>
    private static readonly string[] PathSeparators = [" > ", ">", " / ", "/", "|"];

    /// <summary>
    /// Resolves the group a category belongs to, trying three strategies in order:
    /// an explicit path in the label, the configured grouping, and finally the label
    /// itself. The last case matters — an unrecognised category becomes its own
    /// top-level entry rather than being swept into an "Other" bucket, so nothing
    /// gets hidden just because the mapping doesn't know about it yet.
    /// </summary>
    public static (string Group, string Leaf) Resolve(
        string category, IReadOnlyDictionary<string, string[]> groups)
    {
        foreach (var separator in PathSeparators)
        {
            var index = category.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;

            var group = category[..index].Trim();
            var leaf = category[(index + separator.Length)..].Trim();

            // Deeper paths collapse to first/last — two levels is enough to shop by.
            foreach (var deeper in PathSeparators)
            {
                var tail = leaf.LastIndexOf(deeper, StringComparison.Ordinal);
                if (tail >= 0) leaf = leaf[(tail + deeper.Length)..].Trim();
            }

            if (group.Length > 0 && leaf.Length > 0) return (group, leaf);
        }

        foreach (var (group, members) in groups)
        {
            if (members.Any(m => string.Equals(m, category, StringComparison.CurrentCultureIgnoreCase)))
            {
                return (group, category);
            }
        }

        return (category, category);
    }
}
