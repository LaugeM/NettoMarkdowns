using System.Globalization;
using Microsoft.Data.Sqlite;
using NettoMarkdowns.Models;

namespace NettoMarkdowns.Services;

/// <summary>
/// Remembers what the Products EAN API said about each barcode, so a product is only
/// ever looked up once. Pack size doesn't differ between stores, so rows are keyed by
/// barcode alone. Barcodes Salling doesn't know are remembered too, and asked about
/// again after <see cref="NotFoundRetry"/> in case they get added later.
///
/// Also keeps the day's call count, so a restart doesn't reset the 100 a day budget.
///
/// One row is around 100 bytes and at most 100 are added a day, so the file stays small.
/// </summary>
public sealed class ProductStore
{
    private const int SchemaVersion = 1;
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(30);

    private readonly string _connectionString;

    public ProductStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    /// <summary>A remembered answer. <see cref="Variant"/> is null when Salling had no record.</summary>
    public sealed record Entry(ProductVariant? Variant);

    public Entry? Get(string ean) =>
        GetMany([ean]).TryGetValue(ean, out var entry) ? entry : null;

    /// <summary>Everything known about these barcodes, in as few queries as possible.</summary>
    public Dictionary<string, Entry> GetMany(IEnumerable<string> eans)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var distinct = eans.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToArray();
        if (distinct.Length == 0) return result;

        var retryBefore = Format(DateTimeOffset.UtcNow - NotFoundRetry);

        using var connection = Open();

        // SQLite caps the parameters in one statement, so look barcodes up in chunks.
        foreach (var chunk in distinct.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var names = chunk.Select((_, i) => "$e" + i).ToArray();
            command.CommandText = $"""
                SELECT ean, found, contents, contents_unit, price, unit, unit_price
                FROM product_weights
                WHERE ean IN ({string.Join(",", names)})
                  AND (found = 1 OR fetched_at >= $retryBefore)
                """;
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue(names[i], chunk[i]);
            command.Parameters.AddWithValue("$retryBefore", retryBefore);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var ean = reader.GetString(0);
                result[ean] = reader.GetInt64(1) == 0
                    ? new Entry(null)
                    : new Entry(new ProductVariant
                    {
                        Ean = ean,
                        Contents = NullableDecimal(reader, 2),
                        ContentsUnit = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Price = NullableDecimal(reader, 4),
                        Unit = reader.IsDBNull(5) ? null : reader.GetString(5),
                        UnitPrice = NullableDecimal(reader, 6)
                    });
            }
        }

        return result;
    }

    /// <summary>Saves an answer. Pass null when Salling has no record of the barcode.</summary>
    public void Save(string ean, ProductVariant? variant)
    {
        using var connection = Open();
        Execute(connection, """
            INSERT INTO product_weights (ean, found, contents, contents_unit, price, unit, unit_price, fetched_at)
            VALUES ($ean, $found, $contents, $contentsUnit, $price, $unit, $unitPrice, $now)
            ON CONFLICT (ean) DO UPDATE SET
                found = excluded.found, contents = excluded.contents,
                contents_unit = excluded.contents_unit, price = excluded.price,
                unit = excluded.unit, unit_price = excluded.unit_price,
                fetched_at = excluded.fetched_at
            """,
            ("$ean", ean), ("$found", variant is null ? 0 : 1),
            ("$contents", variant?.Contents), ("$contentsUnit", variant?.ContentsUnit),
            ("$price", variant?.Price), ("$unit", variant?.Unit),
            ("$unitPrice", variant?.UnitPrice), ("$now", Format(DateTimeOffset.UtcNow)));
    }

    public int GetUsage(DateOnly day)
    {
        using var connection = Open();
        var value = Scalar(connection, "SELECT used FROM api_usage WHERE day = $day", ("$day", FormatDay(day)));
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public void SetUsage(DateOnly day, int used)
    {
        using var connection = Open();
        Execute(connection, """
            INSERT INTO api_usage (day, used) VALUES ($day, $used)
            ON CONFLICT (day) DO UPDATE SET used = excluded.used
            """,
            ("$day", FormatDay(day)), ("$used", used));

        // Only today matters; drop older days so the table never grows.
        Execute(connection, "DELETE FROM api_usage WHERE day < $day", ("$day", FormatDay(day)));
    }

    private static decimal? NullableDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatDay(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"));
        if (version >= SchemaVersion) return;

        Execute(connection, $"""
            CREATE TABLE IF NOT EXISTS product_weights (
                ean           TEXT PRIMARY KEY,
                found         INTEGER NOT NULL,  -- 0 when Salling had no record
                contents      REAL,
                contents_unit TEXT,
                price         REAL,
                unit          TEXT,
                unit_price    REAL,
                fetched_at    TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS api_usage (
                day  TEXT PRIMARY KEY,
                used INTEGER NOT NULL
            ) WITHOUT ROWID;

            PRAGMA user_version = {SchemaVersion};
            """);
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }
}
