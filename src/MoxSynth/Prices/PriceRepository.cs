using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Prices;

/// <summary>Read access to prices.db (built by <see cref="PriceImporter"/>).</summary>
public sealed class PriceRepository(DatabasePaths paths)
{
    private const string Columns =
        "uuid, provider, finish, currency, first_date, last_date, price, price_7d, price_30d, price_first, history";

    public bool IsAvailable => File.Exists(paths.PricesPath);
    public string DatabasePath => paths.PricesPath;

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = paths.PricesPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString());

    /// <summary>Prices per uuid → finish for one provider. Missing uuids/finishes are simply absent.</summary>
    public async Task<Dictionary<string, Dictionary<string, PriceInfo>>> GetAsync(IEnumerable<string> uuids, string provider,
        CancellationToken ct = default)
    {
        var ids = uuids.Distinct().ToArray();
        var result = new Dictionary<string, Dictionary<string, PriceInfo>>();
        if (ids.Length == 0 || !IsAvailable) return result;

        await using var conn = Open();
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await conn.QueryAsync<PriceInfo>(new CommandDefinition(
                $"SELECT {Columns} FROM prices WHERE provider = @provider AND uuid IN @chunk",
                new { provider, chunk }, cancellationToken: ct));
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.Uuid, out var byFinish)) result[row.Uuid] = byFinish = new();
                byFinish[row.Finish] = row;
            }
        }
        return result;
    }

    /// <summary>All providers and finishes for one printing (card detail page).</summary>
    public async Task<IReadOnlyList<PriceInfo>> GetAllForCardAsync(string uuid, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        await using var conn = Open();
        var rows = await conn.QueryAsync<PriceInfo>(new CommandDefinition(
            $"SELECT {Columns} FROM prices WHERE uuid = @uuid", new { uuid }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Source date, import time and row count of the current price database (null when there is none).</summary>
    public (string? SourceDate, DateTime? ImportedAtUtc, long Rows, string? Providers)? GetInfo()
    {
        if (!IsAvailable) return null;
        try
        {
            using var conn = Open();
            var meta = conn.Query<(string Key, string? Value)>("SELECT key, value FROM meta").ToDictionary(m => m.Key, m => m.Value);
            var rows = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM prices");
            DateTime? imported = DateTime.TryParse(meta.GetValueOrDefault("imported_at"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var i) ? i : null;
            return (meta.GetValueOrDefault("source_date"), imported, rows, meta.GetValueOrDefault("providers"));
        }
        catch (SqliteException)
        {
            return null; // e.g. while being swapped
        }
    }
}
