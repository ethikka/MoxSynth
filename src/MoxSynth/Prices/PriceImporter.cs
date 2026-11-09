using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MoxSynth.Prices;

/// <summary>
/// Builds prices.db from MTGJSON AllPrices.json.zip (≈1.2 GB of JSON) without unpacking or loading it in memory:
/// the zip entry is streamed through a <see cref="Utf8JsonReader"/>, one card at a time.
/// The result is written to a temporary file and then swapped in, so readers never see a half-built database.
/// </summary>
public static class PriceImporter
{
    public const int SchemaVersion = 1;

    public sealed record Result(string? SourceDate, long Cards, long Rows);

    public static Result Import(string zipPath, string targetPath, IReadOnlyCollection<string> providers,
        Action<long>? progress, CancellationToken ct)
    {
        var tempPath = targetPath + ".importing";
        DeleteDatabaseFiles(tempPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        string? sourceDate = null;
        long cards = 0, rows = 0;

        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = tempPath,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = false,
               }.ToString()))
        {
            conn.Open();
            Exec(conn, """
                PRAGMA journal_mode = OFF;
                PRAGMA synchronous = OFF;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
                CREATE TABLE prices (
                    uuid        TEXT NOT NULL,
                    provider    TEXT NOT NULL,
                    finish      TEXT NOT NULL,
                    currency    TEXT NOT NULL,
                    first_date  TEXT NOT NULL,
                    last_date   TEXT NOT NULL,
                    price       REAL NOT NULL,
                    price_7d    REAL NULL,
                    price_30d   REAL NULL,
                    price_first REAL NULL,
                    history     BLOB NOT NULL,
                    PRIMARY KEY (uuid, provider, finish)
                ) WITHOUT ROWID;
                """);

            var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT OR REPLACE INTO prices
                    (uuid, provider, finish, currency, first_date, last_date, price, price_7d, price_30d, price_first, history)
                VALUES ($uuid, $provider, $finish, $currency, $first, $last, $price, $p7, $p30, $pfirst, $history)
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var name in new[] { "$uuid", "$provider", "$finish", "$currency", "$first", "$last", "$price", "$p7", "$p30", "$pfirst", "$history" })
                p[name] = insert.Parameters.Add(new SqliteParameter { ParameterName = name });

            void Write(string uuid, string provider, string currency, string finish, Series series)
            {
                p["$uuid"].Value = uuid;
                p["$provider"].Value = provider;
                p["$finish"].Value = finish;
                p["$currency"].Value = currency;
                p["$first"].Value = series.FirstDate;
                p["$last"].Value = series.LastDate;
                p["$price"].Value = series.Latest;
                p["$p7"].Value = (object?)series.DaysBefore(7) ?? DBNull.Value;
                p["$p30"].Value = (object?)series.DaysBefore(30) ?? DBNull.Value;
                p["$pfirst"].Value = series.First;
                p["$history"].Value = series.HistoryBlob();
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
                rows++;
            }

            var wanted = providers.ToHashSet(StringComparer.OrdinalIgnoreCase);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The zip file doesn't contain a .json file.");
                using var stream = entry.Open();

                foreach (var (section, key, json) in StreamCards(stream, ct))
                {
                    if (section == "meta")
                    {
                        if (json.RootElement.TryGetProperty("date", out var d)) sourceDate = d.GetString();
                        continue;
                    }

                    // data → uuid → paper → provider → { currency, retail → finish → { date: price } }
                    if (json.RootElement.TryGetProperty("paper", out var paper))
                    {
                        foreach (var provider in paper.EnumerateObject())
                        {
                            if (!wanted.Contains(provider.Name)) continue;
                            var currency = provider.Value.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                            if (!provider.Value.TryGetProperty("retail", out var retail) || retail.ValueKind != JsonValueKind.Object) continue;
                            foreach (var finish in retail.EnumerateObject())
                            {
                                var series = Series.From(finish.Value);
                                if (series is null) continue;
                                Write(key, provider.Name, currency, MapFinish(finish.Name), series);
                            }
                        }
                    }

                    json.Dispose();
                    cards++;
                    if (cards % 5000 == 0)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = conn.BeginTransaction();
                        progress?.Invoke(cards);
                    }
                }
            }

            using (var meta = conn.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT INTO meta (key, value) VALUES ('source_date', $d), ('imported_at', $i), ('schema', $s), ('providers', $p)";
                meta.Parameters.AddWithValue("$d", (object?)sourceDate ?? DBNull.Value);
                meta.Parameters.AddWithValue("$i", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                meta.Parameters.AddWithValue("$s", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                meta.Parameters.AddWithValue("$p", string.Join(",", providers));
                meta.ExecuteNonQuery();
            }
            tx.Commit();
            tx.Dispose();
            Exec(conn, "ANALYZE;");
        }

        ct.ThrowIfCancellationRequested();

        // Swap in the new database; open read-only connections to the old file are released first.
        SqliteConnection.ClearAllPools();
        DeleteDatabaseFiles(targetPath, keepMain: true);
        File.Move(tempPath, targetPath, overwrite: true);
        progress?.Invoke(cards);
        return new Result(sourceDate, cards, rows);
    }

    private static string MapFinish(string mtgjson) => mtgjson switch
    {
        "normal" => "nonfoil",
        _ => mtgjson, // foil, etched
    };

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void DeleteDatabaseFiles(string path, bool keepMain = false)
    {
        foreach (var suffix in keepMain ? new[] { "-wal", "-shm", "-journal" } : new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    // ------------------------------------------------------------------ streaming JSON

    /// <summary>
    /// Yields ("meta", "", meta object) and then ("data", uuid, card object) for every card, reading the stream in
    /// chunks. Each yielded JsonDocument must be disposed by the caller.
    /// </summary>
    private static IEnumerable<(string Section, string Key, JsonDocument Json)> StreamCards(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4 * 1024 * 1024];
        var filled = 0;
        var eof = false;
        var state = new JsonReaderState();

        bool Fill()
        {
            if (eof) return false;
            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // a single value larger than the buffer
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) eof = true;
            filled += read;
            return read > 0;
        }

        Fill();
        string? section = null; // "meta" or "data" while inside those top-level objects

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var results = new List<(string, string, JsonDocument)>();
            var consumed = ParseChunk(buffer.AsSpan(0, filled), eof, ref state, ref section, results);
            foreach (var r in results) yield return r;

            if (consumed == 0 && eof) yield break;
            // Keep the unconsumed tail, refill behind it.
            Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
            filled -= consumed;
            if (!Fill() && filled == 0) yield break;
            if (eof && consumed == 0 && results.Count == 0) yield break;
        }
    }

    /// <summary>Parses as many complete top-level items as fit in <paramref name="data"/>; returns bytes consumed.</summary>
    private static int ParseChunk(ReadOnlySpan<byte> data, bool isFinal, ref JsonReaderState state, ref string? section,
        List<(string, string, JsonDocument)> results)
    {
        var reader = new Utf8JsonReader(data, isFinal, state);
        long safe = 0; // everything before this offset has been fully handled
        var safeState = state;

        while (true)
        {
            var checkpoint = reader; // copy: lets us rewind to here when a value is incomplete
            if (!reader.Read()) break;

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject when reader.CurrentDepth == 0:
                case JsonTokenType.EndObject:
                    break;

                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    // "meta": {...} → parse whole; "data": { → descend
                    var top = reader.GetString();
                    if (!reader.Read()) { reader = checkpoint; goto done; }
                    if (top == "data" && reader.TokenType == JsonTokenType.StartObject)
                    {
                        section = "data";
                        break;
                    }
                    var valueStart = (int)reader.TokenStartIndex;
                    if (!reader.TrySkip()) { reader = checkpoint; goto done; }
                    if (top == "meta")
                        results.Add(("meta", "", JsonDocument.Parse(data[valueStart..(int)reader.BytesConsumed].ToArray())));
                    break;

                case JsonTokenType.PropertyName when reader.CurrentDepth == 2 && section == "data":
                    var uuid = reader.GetString()!;
                    if (!reader.Read()) { reader = checkpoint; goto done; }
                    var start = (int)reader.TokenStartIndex;
                    if (!reader.TrySkip()) { reader = checkpoint; goto done; }
                    results.Add(("data", uuid, JsonDocument.Parse(data[start..(int)reader.BytesConsumed].ToArray())));
                    break;
            }

            safe = reader.BytesConsumed;
            safeState = reader.CurrentState;
        }

        done:
        state = safeState;
        return (int)safe;
    }

    // ------------------------------------------------------------------ series

    private sealed class Series
    {
        private readonly List<(DateOnly Date, double Price)> _points;

        private Series(List<(DateOnly, double)> points) => _points = points;

        public string FirstDate => _points[0].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string LastDate => _points[^1].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public double Latest => _points[^1].Price;
        public double First => _points[0].Price;

        public static Series? From(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            var points = new List<(DateOnly, double)>();
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number &&
                    DateOnly.TryParseExact(p.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    points.Add((date, p.Value.GetDouble()));
            }
            if (points.Count == 0) return null;
            points.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return new Series(points);
        }

        /// <summary>Price on the last day at least <paramref name="days"/> before the latest price.</summary>
        public double? DaysBefore(int days)
        {
            var target = _points[^1].Date.AddDays(-days);
            for (var i = _points.Count - 1; i >= 0; i--)
                if (_points[i].Date <= target) return _points[i].Price;
            return null;
        }

        /// <summary>float32 per day from first to last date; NaN for days without a price.</summary>
        public byte[] HistoryBlob()
        {
            var first = _points[0].Date;
            var length = _points[^1].Date.DayNumber - first.DayNumber + 1;
            var values = new float[length];
            Array.Fill(values, float.NaN);
            foreach (var (date, price) in _points) values[date.DayNumber - first.DayNumber] = (float)price;
            var blob = new byte[length * 4];
            Buffer.BlockCopy(values, 0, blob, 0, blob.Length);
            return blob;
        }
    }
}
