using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Cards;

/// <summary>A physical card printing (primary face) as needed by importers.</summary>
public sealed record CardRef(
    string Uuid, string Name, string SetCode, string Number, string? Finishes, string? Language, string? ReleaseDate,
    string? SetType = null, bool IsPaper = true, bool IsPromo = false)
{
    public IReadOnlyList<string> FinishList => CardFinishes.Parse(Finishes);

    /// <summary>A regular paper printing (not a promo, masterpiece, token, online-only, …).</summary>
    public bool IsRegularPaper => IsPaper && !IsPromo &&
        SetType is "expansion" or "core" or "masters" or "draft_innovation" or "commander" or "starter" or "duel_deck";
}

/// <summary>
/// In-memory lookup tables over AllPrintings for bulk matching (imports): by Scryfall id, by set + collector number
/// and by (face) name. Built on first use (~1–2 s) and kept for the lifetime of the app.
/// </summary>
public sealed class CardIndex(DatabasePaths paths, ILogger<CardIndex> logger)
{
    private sealed class Row
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? FaceName { get; set; }
        public string SetCode { get; set; } = "";
        public string Number { get; set; } = "";
        public string? Finishes { get; set; }
        public string? Language { get; set; }
        public string? ReleaseDate { get; set; }
        public string? ScryfallId { get; set; }
        public string? SetType { get; set; }
        public string? Availability { get; set; }
        public long? IsPromo { get; set; }
    }

    private readonly Lazy<Tables> _tables = new(() => Load(paths, logger), LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Tables(
        Dictionary<string, CardRef> ByScryfallId,
        Dictionary<(string Set, string Number), CardRef> BySetNumber,
        Dictionary<string, List<CardRef>> ByName);

    public CardRef? ByScryfallId(string? scryfallId) =>
        string.IsNullOrWhiteSpace(scryfallId) ? null : _tables.Value.ByScryfallId.GetValueOrDefault(scryfallId.Trim().ToLowerInvariant());

    public CardRef? BySetAndNumber(string? setCode, string? number)
    {
        if (string.IsNullOrWhiteSpace(setCode) || string.IsNullOrWhiteSpace(number)) return null;
        var set = setCode.Trim().ToUpperInvariant();
        var n = number.Trim();
        var t = _tables.Value.BySetNumber;
        return t.GetValueOrDefault((set, n)) ?? t.GetValueOrDefault((set, n.TrimStart('0')));
    }

    /// <summary>All printings with this card or face name (case-insensitive), newest set first.</summary>
    public IReadOnlyList<CardRef> ByName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? [] : _tables.Value.ByName.GetValueOrDefault(NormalizeName(name)) ?? [];

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    private static Tables Load(DatabasePaths paths, ILogger logger)
    {
        var started = DateTime.UtcNow;
        var byScryfall = new Dictionary<string, CardRef>(StringComparer.OrdinalIgnoreCase);
        var bySetNumber = new Dictionary<(string, string), CardRef>();
        var byName = new Dictionary<string, List<CardRef>>();

        if (File.Exists(paths.AllPrintingsPath))
        {
            using var conn = new SqliteConnection(paths.AllPrintingsConnectionString);
            var rows = conn.Query<Row>("""
                SELECT c.uuid, c.name, c.faceName, c.setCode, c.number, c.finishes, c.language, s.releaseDate, ci.scryfallId,
                       s.type AS SetType, c.availability, c.isPromo
                FROM cards c
                LEFT JOIN sets s ON s.code = c.setCode
                LEFT JOIN cardIdentifiers ci ON ci.uuid = c.uuid
                WHERE c.side IS NULL OR c.side = 'a'
                ORDER BY s.releaseDate DESC
                """);

            foreach (var r in rows)
            {
                var card = new CardRef(r.Uuid, r.Name, r.SetCode, r.Number, r.Finishes, r.Language, r.ReleaseDate,
                    r.SetType, (r.Availability ?? "").Contains("paper"), r.IsPromo == 1);
                if (!string.IsNullOrEmpty(r.ScryfallId)) byScryfall.TryAdd(r.ScryfallId.ToLowerInvariant(), card);
                bySetNumber.TryAdd((r.SetCode.ToUpperInvariant(), r.Number), card);

                foreach (var name in new[] { r.Name, r.FaceName }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
                {
                    var key = NormalizeName(name!);
                    if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
                    list.Add(card);
                }
            }
        }

        logger.LogInformation("Card index built: {Count:N0} printings in {Ms:N0} ms.", bySetNumber.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        return new Tables(byScryfall, bySetNumber, byName);
    }
}
