using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Cards;

/// <summary>
/// Read-only access to the MTGJSON AllPrintings.sqlite card catalogue.
/// The file is never written to, so it can be replaced by a newer MTGJSON release at any time
/// (MTGJSON v5 uuids are stable between releases).
/// </summary>
public sealed class CardRepository(DatabasePaths paths)
{
    // Multi-face cards have one row per face; the "a" side (or no side) represents the physical card.
    private const string PrimaryFaceFilter = "(c.side IS NULL OR c.side = 'a')";

    private const string SummaryColumns = """
        c.uuid, c.name, c.setCode, s.name AS SetName, s.keyruneCode, s.releaseDate, c.number,
        c.rarity, c.manaCost, c.type, c.finishes, c.language, c.layout, ci.scryfallId
        """;

    private const string SummaryFrom = """
        FROM cards c
        LEFT JOIN sets s ON s.code = c.setCode
        LEFT JOIN cardIdentifiers ci ON ci.uuid = c.uuid
        """;

    private const string FaceColumns = """
        c.uuid, c.name, c.faceName, c.side, c.manaCost, c.type, c.text, c.flavorText, c.power, c.toughness,
        c.loyalty, c.defense, c.artist, c.rarity, c.setCode, s.name AS SetName, s.keyruneCode, s.releaseDate,
        c.number, c.finishes, c.layout, c.language, c.otherFaceIds, ci.scryfallId
        """;

    private IReadOnlyList<SetInfo>? _sets;
    private IReadOnlySet<string>? _formats;

    public bool IsAvailable => File.Exists(paths.AllPrintingsPath);
    public string DatabasePath => paths.AllPrintingsPath;

    private SqliteConnection Open() => new(paths.AllPrintingsConnectionString);

    /// <summary>Format names (columns of cardLegalities), used to whitelist f:/legal:/banned: in Scryfall queries.</summary>
    private IReadOnlySet<string> Formats
    {
        get
        {
            if (_formats is not null) return _formats;
            if (!IsAvailable) return new HashSet<string>();
            using var conn = Open();
            var columns = conn.Query<string>("SELECT name FROM pragma_table_info('cardLegalities') WHERE name <> 'uuid'");
            return _formats = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Parses a Scryfall-syntax query (for previewing the interpretation and warnings in the UI).</summary>
    public ScryfallFilter ParseScryfall(string? query) => ScryfallQueryParser.Parse(query, Formats);

    public async Task<PagedResult<CardSummary>> SearchAsync(CardSearchQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        if (!IsAvailable) return new PagedResult<CardSummary>([], 0, page, pageSize);

        var where = new List<string> { PrimaryFaceFilter };
        var p = new DynamicParameters();

        var nameSearch = query.NameSearch;
        if (nameSearch.CollectorNumber is { } number)
        {
            // Numbers in the name box → collector number prefix search ("12" finds 12, 120–129, 12a, …).
            where.Add(@"c.number LIKE @numberPrefix ESCAPE '\'");
            p.Add("numberPrefix", Contains(number)[1..]);
            p.Add("numberExact", number);
        }
        for (var i = 0; i < nameSearch.NameParts.Count; i++)
        {
            // Every word must occur somewhere in the name (any order): "bolt light" finds Lightning Bolt.
            where.Add($@"(c.name LIKE @name{i} ESCAPE '\' OR c.asciiName LIKE @name{i} ESCAPE '\' OR c.faceName LIKE @name{i} ESCAPE '\')");
            p.Add($"name{i}", Contains(nameSearch.NameParts[i]));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add(@"c.text LIKE @text ESCAPE '\'");
            p.Add("text", Contains(query.Text));
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            where.Add(@"c.type LIKE @type ESCAPE '\'");
            p.Add("type", Contains(query.Type));
        }
        if (!string.IsNullOrWhiteSpace(query.SetCode))
        {
            where.Add("c.setCode = @setCode");
            p.Add("setCode", query.SetCode.Trim().ToUpperInvariant());
        }
        if (!string.IsNullOrWhiteSpace(query.Rarity))
        {
            where.Add("c.rarity = @rarity");
            p.Add("rarity", query.Rarity.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(query.Colors))
        {
            var colors = query.Colors.ToUpperInvariant();
            if (colors.Contains('C'))
            {
                where.Add("(c.colors IS NULL OR c.colors = '')");
            }
            else
            {
                var i = 0;
                foreach (var color in colors.Where(ch => "WUBRG".Contains(ch)).Distinct())
                {
                    where.Add($"c.colors LIKE @color{i}");
                    p.Add($"color{i}", $"%{color}%");
                    i++;
                }
            }
        }
        if (!query.IncludeNonPaper)
        {
            where.Add("c.availability LIKE '%paper%'");
        }

        var scryfall = ParseScryfall(query.Scryfall);
        if (!scryfall.HasEffect && !query.HasCriteriaBesidesScryfall)
        {
            // Only an incomplete Scryfall query (e.g. "t:") – don't list the whole catalogue.
            return new PagedResult<CardSummary>([], 0, page, pageSize);
        }
        if (scryfall.WhereSql is not null)
        {
            // Evaluated against every face (so o:flying finds a DFC whose back face flies), then mapped to the
            // physical card: side "a" itself, or for other sides the first uuid in otherFaceIds (the front face).
            where.Add($"""
                c.uuid IN (
                    SELECT CASE WHEN f.side IS NULL OR f.side = 'a' THEN f.uuid ELSE substr(f.otherFaceIds, 1, 36) END
                    FROM cards f
                    LEFT JOIN sets fs ON fs.code = f.setCode
                    WHERE {scryfall.WhereSql})
                """);
            p.AddDynamicParams(scryfall.Parameters);
        }

        var whereSql = string.Join(" AND ", where);

        // Ranking: exact collector number first; then names that equal / start with what was typed
        // ("Lightning Bolt" before "Emeritus of Conflict // Lightning Bolt"). Then numeric order when only a
        // number (or only a set) is searched, otherwise alphabetical.
        var order = new List<string>();
        if (nameSearch.CollectorNumber is not null)
        {
            order.Add("CASE WHEN c.number = @numberExact COLLATE NOCASE THEN 0 ELSE 1 END");
        }
        if (nameSearch.NameParts.Count > 0)
        {
            p.Add("namePhrase", nameSearch.Phrase);
            p.Add("namePhrasePrefix", Contains(nameSearch.Phrase)[1..]);   // "phrase%"
            // Name (or face name) starting with any of the words: "bolt light" ranks Lightning Bolt
            // above Emeritus of Conflict // Lightning Bolt.
            var startsWithAnyWord = new List<string>();
            for (var i = 0; i < nameSearch.NameParts.Count; i++)
            {
                p.Add($"nameStart{i}", Contains(nameSearch.NameParts[i])[1..]); // "word%"
                startsWithAnyWord.Add($@"c.name LIKE @nameStart{i} ESCAPE '\' OR c.faceName LIKE @nameStart{i} ESCAPE '\'");
            }
            order.Add($"""
                CASE
                    WHEN c.name = @namePhrase COLLATE NOCASE OR c.faceName = @namePhrase COLLATE NOCASE THEN 0
                    WHEN c.name LIKE @namePhrasePrefix ESCAPE '\' OR c.faceName LIKE @namePhrasePrefix ESCAPE '\' THEN 1
                    WHEN {string.Join(" OR ", startsWithAnyWord)} THEN 2
                    ELSE 3
                END
                """);
            order.Add("c.name COLLATE NOCASE, s.releaseDate DESC, c.setCode, CAST(c.number AS INTEGER), c.number");
        }
        else if (nameSearch.CollectorNumber is not null)
        {
            order.Add("CAST(c.number AS INTEGER), c.number, s.releaseDate DESC, c.setCode");
        }
        else if (!string.IsNullOrWhiteSpace(query.SetCode))
        {
            order.Add("CAST(c.number AS INTEGER), c.number");
        }
        else
        {
            order.Add("c.name COLLATE NOCASE, s.releaseDate DESC, c.setCode, CAST(c.number AS INTEGER), c.number");
        }
        var orderBy = scryfall.OrderBy is not null
            ? $"{scryfall.OrderBy}, c.name COLLATE NOCASE, s.releaseDate DESC" // explicit order: in the Scryfall query wins
            : string.Join(", ", order);

        p.Add("take", pageSize);
        p.Add("skip", (page - 1) * pageSize);

        await using var conn = Open();
        var total = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition($"SELECT COUNT(*) FROM cards c WHERE {whereSql}", p, cancellationToken: ct));

        var items = await conn.QueryAsync<CardSummary>(new CommandDefinition($"""
            SELECT {SummaryColumns}
            {SummaryFrom}
            WHERE {whereSql}
            ORDER BY {orderBy}
            LIMIT @take OFFSET @skip
            """, p, cancellationToken: ct));

        return new PagedResult<CardSummary>(items.AsList(), (int)total, page, pageSize);
    }

    public async Task<CardDetails?> GetDetailsAsync(string uuid, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        await using var conn = Open();

        var face = await conn.QuerySingleOrDefaultAsync<CardFace>(new CommandDefinition($"""
            SELECT {FaceColumns}
            {SummaryFrom}
            WHERE c.uuid = @uuid
            """, new { uuid }, cancellationToken: ct));
        if (face is null) return null;

        var faces = new List<CardFace> { face };
        var otherIds = (face.OtherFaceIds ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (otherIds.Length > 0)
        {
            faces.AddRange(await conn.QueryAsync<CardFace>(new CommandDefinition($"""
                SELECT {FaceColumns}
                {SummaryFrom}
                WHERE c.uuid IN @otherIds
                """, new { otherIds }, cancellationToken: ct)));
        }

        // Keep only faces of the same physical printing (meld cards reference other printings), ordered a, b, c…
        faces = faces
            .Where(f => f.Uuid == face.Uuid || (f.SetCode == face.SetCode && f.Number == face.Number))
            .DistinctBy(f => f.Uuid)
            .OrderBy(f => f.Side ?? "")
            .ToList();

        var primary = faces.FirstOrDefault(f => f.Side is null or "a") ?? face;

        var printings = await conn.QueryAsync<CardSummary>(new CommandDefinition($"""
            SELECT {SummaryColumns}
            {SummaryFrom}
            WHERE c.name = @name AND {PrimaryFaceFilter}
            ORDER BY s.releaseDate DESC, c.setCode, CAST(c.number AS INTEGER), c.number
            """, new { name = primary.Name }, cancellationToken: ct));

        return new CardDetails
        {
            Primary = primary,
            Faces = faces,
            OtherPrintings = printings.Where(x => x.Uuid != primary.Uuid).ToList(),
        };
    }

    public async Task<Dictionary<string, CardSummary>> GetSummariesAsync(IEnumerable<string> uuids, CancellationToken ct = default)
    {
        var ids = uuids.Distinct().ToArray();
        if (ids.Length == 0 || !IsAvailable) return new();

        await using var conn = Open();
        var rows = await conn.QueryAsync<CardSummary>(new CommandDefinition($"""
            SELECT {SummaryColumns}
            {SummaryFrom}
            WHERE c.uuid IN @ids
            """, new { ids }, cancellationToken: ct));
        return rows.DistinctBy(r => r.Uuid).ToDictionary(r => r.Uuid);
    }

    public async Task<IReadOnlyList<SetInfo>> GetSetsAsync(CancellationToken ct = default)
    {
        if (_sets is not null) return _sets;
        if (!IsAvailable) return [];

        await using var conn = Open();
        var sets = await conn.QueryAsync<SetInfo>(new CommandDefinition("""
            SELECT code, name, releaseDate, type, keyruneCode
            FROM sets
            ORDER BY releaseDate DESC, name
            """, cancellationToken: ct));
        return _sets = sets.AsList();
    }

    public async Task<CardDbMeta?> GetMetaAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        await using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<CardDbMeta>(
            new CommandDefinition("SELECT date, version FROM meta LIMIT 1", cancellationToken: ct));
    }

    private static string Contains(string value)
    {
        var sb = new StringBuilder("%");
        foreach (var ch in value.Trim())
        {
            if (ch is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.Append('%').ToString();
    }
}
