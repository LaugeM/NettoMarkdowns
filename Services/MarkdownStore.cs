using System.Globalization;
using Microsoft.Data.Sqlite;
using NettoMarkdowns.Models;

namespace NettoMarkdowns.Services;

/// <summary>
/// The SQLite history behind the markdown logger.
///
/// An offer is identified by store, barcode and the API's startTime. Seeing the
/// same one again updates it; a new startTime for the same barcode is a new
/// markdown and gets its own row. When an offer is missing from a store's
/// response it is marked gone, so last_seen..gone_at brackets when it sold out
/// or was pulled.
///
/// All times are stored as UTC ISO 8601 text, which sorts correctly as a string.
/// </summary>
public sealed class MarkdownStore
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;

    public MarkdownStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    public sealed record PollResult(int Stores, int Offers, int New, int Gone);

    /// <summary>Writes one successful poll. Everything happens in one transaction.</summary>
    public PollResult RecordPoll(DateTimeOffset seenAt, IReadOnlyList<StoreClearances> stores)
    {
        var now = Format(seenAt);
        int offers = 0, added = 0, gone = 0;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var entry in stores)
        {
            var store = entry.Store;
            if (string.IsNullOrWhiteSpace(store.Id)) continue;

            Execute(connection, """
                INSERT INTO stores (id, name, brand, street, zip, city, first_seen, last_seen)
                VALUES ($id, $name, $brand, $street, $zip, $city, $now, $now)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name, brand = excluded.brand, street = excluded.street,
                    zip = excluded.zip, city = excluded.city, last_seen = excluded.last_seen
                """,
                ("$id", store.Id), ("$name", store.Name), ("$brand", store.Brand),
                ("$street", store.Address?.Street), ("$zip", store.Address?.Zip),
                ("$city", store.Address?.City), ("$now", now));

            var open = LoadOpenOffers(connection, store.Id);
            var seen = new HashSet<long>();

            foreach (var clearance in entry.Clearances)
            {
                var offer = clearance.Offer;
                var key = (Ean: KeyFor(clearance), Start: offer.StartTime is { } s ? Format(s) : "");

                var id = (long)Scalar(connection, """
                    INSERT INTO offers (store_id, ean, start_time, description, category,
                        original_price, new_price, percent_discount, stock, stock_unit,
                        end_time, first_seen, last_seen)
                    VALUES ($store, $ean, $start, $description, $category,
                        $original, $new, $percent, $stock, $unit,
                        $end, $now, $now)
                    ON CONFLICT (store_id, ean, start_time) DO UPDATE SET
                        description = excluded.description, category = excluded.category,
                        original_price = excluded.original_price, new_price = excluded.new_price,
                        percent_discount = excluded.percent_discount, stock = excluded.stock,
                        stock_unit = excluded.stock_unit, end_time = excluded.end_time,
                        last_seen = excluded.last_seen, gone_at = NULL
                    RETURNING id
                    """,
                    ("$store", store.Id), ("$ean", key.Ean), ("$start", key.Start),
                    ("$description", clearance.Product.Description),
                    ("$category", clearance.Product.Category),
                    ("$original", offer.OriginalPrice), ("$new", offer.NewPrice),
                    ("$percent", offer.PercentDiscount), ("$stock", offer.Stock),
                    ("$unit", offer.StockUnit),
                    ("$end", offer.EndTime is { } e ? Format(e) : null),
                    ("$now", now))!;

                // The API can list the same offer twice; count it once.
                if (!seen.Add(id)) continue;
                offers++;

                var known = open.TryGetValue(key, out var before);
                if (!known) added++;

                // A history row whenever price or stock moves, so sell-through can be read later.
                if (!known || before.Price != offer.NewPrice || before.Stock != offer.Stock)
                {
                    Execute(connection, """
                        INSERT INTO offer_changes (offer_id, seen_at, new_price, stock)
                        VALUES ($id, $now, $price, $stock)
                        """,
                        ("$id", id), ("$now", now), ("$price", offer.NewPrice), ("$stock", offer.Stock));
                }
            }

            foreach (var (_, (id, _, _)) in open)
            {
                if (seen.Contains(id)) continue;
                Execute(connection, "UPDATE offers SET gone_at = $now WHERE id = $id",
                    ("$now", now), ("$id", id));
                gone++;
            }
        }

        var result = new PollResult(stores.Count, offers, added, gone);
        Execute(connection, """
            INSERT INTO polls (at, stores, offers, new_offers, gone)
            VALUES ($at, $stores, $offers, $new, $gone)
            """,
            ("$at", now), ("$stores", result.Stores), ("$offers", result.Offers),
            ("$new", result.New), ("$gone", result.Gone));

        transaction.Commit();
        return result;
    }

    /// <summary>Records a poll that failed, so gaps in the data can be told apart from quiet days.</summary>
    public void RecordFailedPoll(DateTimeOffset at, string error)
    {
        using var connection = Open();
        Execute(connection, "INSERT INTO polls (at, error) VALUES ($at, $error)",
            ("$at", Format(at)), ("$error", error));
    }

    private Dictionary<(string Ean, string Start), (long Id, decimal Price, double Stock)> LoadOpenOffers(
        SqliteConnection connection, string storeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ean, start_time, new_price, stock
            FROM offers WHERE store_id = $store AND gone_at IS NULL
            """;
        command.Parameters.AddWithValue("$store", storeId);

        var open = new Dictionary<(string, string), (long, decimal, double)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            open[(reader.GetString(1), reader.GetString(2))] =
                (reader.GetInt64(0), reader.GetDecimal(3), reader.GetDouble(4));
        }
        return open;
    }

    /// <summary>The barcode, or the description when an offer has none.</summary>
    private static string KeyFor(Clearance clearance) =>
        clearance.Offer.Ean is { Length: > 0 } ean ? ean :
        clearance.Product.Ean is { Length: > 0 } productEan ? productEan :
        "desc:" + (clearance.Product.Description ?? "");

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

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

        // WAL lets a future stats page read while the logger writes.
        Execute(connection, $"""
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS stores (
                id         TEXT PRIMARY KEY,
                name       TEXT,
                brand      TEXT,
                street     TEXT,
                zip        TEXT,
                city       TEXT,
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS offers (
                id               INTEGER PRIMARY KEY,
                store_id         TEXT NOT NULL REFERENCES stores (id),
                ean              TEXT NOT NULL,
                start_time       TEXT NOT NULL,  -- '' when the API gave none
                description      TEXT,
                category         TEXT,
                original_price   REAL,
                new_price        REAL,
                percent_discount REAL,
                stock            REAL,
                stock_unit       TEXT,
                end_time         TEXT,
                first_seen       TEXT NOT NULL,
                last_seen        TEXT NOT NULL,
                gone_at          TEXT,           -- first poll it was missing from
                UNIQUE (store_id, ean, start_time)
            );

            CREATE INDEX IF NOT EXISTS ix_offers_open ON offers (store_id) WHERE gone_at IS NULL;

            CREATE TABLE IF NOT EXISTS offer_changes (
                offer_id  INTEGER NOT NULL REFERENCES offers (id),
                seen_at   TEXT NOT NULL,
                new_price REAL,
                stock     REAL
            );

            CREATE INDEX IF NOT EXISTS ix_offer_changes_offer ON offer_changes (offer_id);

            CREATE TABLE IF NOT EXISTS polls (
                id         INTEGER PRIMARY KEY,
                at         TEXT NOT NULL,
                stores     INTEGER,
                offers     INTEGER,
                new_offers INTEGER,
                gone       INTEGER,
                error      TEXT
            );

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
