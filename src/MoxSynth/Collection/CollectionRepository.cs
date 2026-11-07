using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Collection;

public sealed class CollectionRepository(DatabasePaths paths)
{
    private const string CardColumns = """
        id, card_uuid, name, set_code, number, finish, condition, language,
        purchase_price, notes, added_at, removed_at, removal_reason
        """;

    private SqliteConnection Open() => new(paths.CollectionConnectionString);

    /// <summary>Adds <see cref="AddCardsRequest.Quantity"/> individual cards, all sharing the same added-at timestamp.</summary>
    public async Task<IReadOnlyList<long>> AddAsync(AddCardsRequest request, CancellationToken ct = default)
    {
        if (request.Quantity is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request), "Quantity must be between 1 and 1000.");

        var addedAt = SqliteTime.ToDb(request.AddedAt ?? DateTime.UtcNow);
        var args = new
        {
            request.CardUuid,
            request.Name,
            request.SetCode,
            request.Number,
            request.Finish,
            Condition = request.Condition.ToString(),
            request.Language,
            request.PurchasePrice,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            AddedAt = addedAt,
        };

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ids = new List<long>(request.Quantity);
        for (var i = 0; i < request.Quantity; i++)
        {
            ids.Add(await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO collection_cards
                    (card_uuid, name, set_code, number, finish, condition, language, purchase_price, notes, added_at)
                VALUES
                    (@CardUuid, @Name, @SetCode, @Number, @Finish, @Condition, @Language, @PurchasePrice, @Notes, @AddedAt);
                SELECT last_insert_rowid();
                """, args, tx, cancellationToken: ct)));
        }

        await tx.CommitAsync(ct);
        return ids;
    }

    /// <summary>Marks a single card as removed. Returns false if it was already removed or does not exist.</summary>
    public async Task<bool> RemoveAsync(long id, string? reason = null, DateTime? removedAt = null, CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE collection_cards
            SET removed_at = MAX(@removedAt, added_at), removal_reason = @reason
            WHERE id = @id AND removed_at IS NULL
            """, new { id, reason, removedAt = SqliteTime.ToDb(removedAt ?? DateTime.UtcNow) }, cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>Removes the oldest owned copy of the given printing/finish/condition/language (FIFO).</summary>
    public async Task<bool> RemoveOneAsync(string cardUuid, string finish, CardCondition condition, string language,
        string? reason = null, CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE collection_cards
            SET removed_at = MAX(@removedAt, added_at), removal_reason = @reason
            WHERE id = (
                SELECT id FROM collection_cards
                WHERE card_uuid = @cardUuid AND finish = @finish AND condition = @condition
                  AND language = @language AND removed_at IS NULL
                ORDER BY added_at, id
                LIMIT 1)
            """, new
        {
            cardUuid,
            finish,
            condition = condition.ToString(),
            language,
            reason,
            removedAt = SqliteTime.ToDb(DateTime.UtcNow),
        }, cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>Undoes a removal (e.g. when it was entered by mistake).</summary>
    public async Task<bool> RestoreAsync(long id, CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE collection_cards SET removed_at = NULL, removal_reason = NULL
            WHERE id = @id AND removed_at IS NOT NULL
            """, new { id }, cancellationToken: ct));
        return rows == 1;
    }

    public async Task<IReadOnlyList<CollectionCard>> GetCopiesAsync(string cardUuid, bool includeRemoved, CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<CollectionCard>(new CommandDefinition($"""
            SELECT {CardColumns}
            FROM collection_cards
            WHERE card_uuid = @cardUuid {(includeRemoved ? "" : "AND removed_at IS NULL")}
            ORDER BY removed_at IS NOT NULL, added_at DESC, id DESC
            """, new { cardUuid }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Number of currently owned copies per uuid (uuids without copies are omitted).</summary>
    public async Task<Dictionary<string, long>> GetOwnedCountsAsync(IEnumerable<string> cardUuids, CancellationToken ct = default)
    {
        var ids = cardUuids.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = Open();
        var rows = await conn.QueryAsync<(string CardUuid, long Count)>(new CommandDefinition("""
            SELECT card_uuid, COUNT(*)
            FROM collection_cards
            WHERE removed_at IS NULL AND card_uuid IN @ids
            GROUP BY card_uuid
            """, new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.CardUuid, r => r.Count);
    }

    public async Task<CollectionPage> GetGroupsAsync(CollectionQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var where = new List<string>();
        var p = new DynamicParameters();

        if (query.AsOf is { } asOf)
        {
            where.Add("added_at <= @asOf AND (removed_at IS NULL OR removed_at > @asOf)");
            p.Add("asOf", SqliteTime.ToDb(asOf));
        }
        else
        {
            where.Add("removed_at IS NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            where.Add(@"name LIKE @name ESCAPE '\'");
            p.Add("name", "%" + query.Name.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
        }
        if (!string.IsNullOrWhiteSpace(query.SetCode))
        {
            where.Add("set_code = @setCode");
            p.Add("setCode", query.SetCode);
        }
        if (!string.IsNullOrWhiteSpace(query.Finish))
        {
            where.Add("finish = @finish");
            p.Add("finish", query.Finish);
        }

        var whereSql = string.Join(" AND ", where);
        const string groupBy = "GROUP BY card_uuid, finish, condition, language";
        p.Add("take", pageSize);
        p.Add("skip", (page - 1) * pageSize);

        await using var conn = Open();
        var totals = await conn.QuerySingleAsync<(long Groups, long Copies)>(new CommandDefinition($"""
            SELECT COUNT(*), COALESCE(SUM(qty), 0)
            FROM (SELECT COUNT(*) AS qty FROM collection_cards WHERE {whereSql} {groupBy})
            """, p, cancellationToken: ct));

        var groups = await conn.QueryAsync<CollectionGroup>(new CommandDefinition($"""
            SELECT card_uuid, MAX(name) AS name, MAX(set_code) AS set_code, MAX(number) AS number,
                   finish, condition, language, COUNT(*) AS quantity,
                   MIN(added_at) AS first_added_at, MAX(added_at) AS last_added_at
            FROM collection_cards
            WHERE {whereSql}
            {groupBy}
            ORDER BY MAX(name) COLLATE NOCASE, MAX(set_code), MAX(number), finish, condition
            LIMIT @take OFFSET @skip
            """, p, cancellationToken: ct));

        return new CollectionPage(
            new PagedResult<CollectionGroup>(groups.AsList(), (int)totals.Groups, page, pageSize),
            totals.Copies);
    }

    public async Task<IReadOnlyList<string>> GetSetCodesAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT set_code FROM collection_cards ORDER BY set_code", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<PagedResult<HistoryEvent>> GetHistoryAsync(int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        await using var conn = Open();
        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) + COUNT(removed_at) FROM collection_cards", cancellationToken: ct));

        var rows = await conn.QueryAsync<HistoryEvent>(new CommandDefinition("""
            SELECT * FROM (
                SELECT id, card_uuid, name, set_code, number, finish, condition,
                       'Added' AS kind, NULL AS removal_reason, added_at AS at
                FROM collection_cards
                UNION ALL
                SELECT id, card_uuid, name, set_code, number, finish, condition,
                       'Removed' AS kind, removal_reason, removed_at AS at
                FROM collection_cards
                WHERE removed_at IS NOT NULL
            )
            ORDER BY at DESC, id DESC
            LIMIT @take OFFSET @skip
            """, new { take = pageSize, skip = (page - 1) * pageSize }, cancellationToken: ct));

        return new PagedResult<HistoryEvent>(rows.AsList(), (int)total, page, pageSize);
    }

    public async Task<CollectionStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        return await conn.QuerySingleAsync<CollectionStats>(new CommandDefinition("""
            SELECT
                (SELECT COUNT(*) FROM collection_cards WHERE removed_at IS NULL)                   AS owned_copies,
                (SELECT COUNT(DISTINCT card_uuid) FROM collection_cards WHERE removed_at IS NULL)  AS unique_printings,
                (SELECT COUNT(DISTINCT name) FROM collection_cards WHERE removed_at IS NULL)       AS unique_names,
                (SELECT COUNT(*) FROM collection_cards WHERE removed_at IS NOT NULL)               AS removed_copies,
                (SELECT COUNT(*) FROM collection_cards WHERE added_at >= @since)                   AS added_last30_days,
                (SELECT COUNT(*) FROM collection_cards WHERE removed_at >= @since)                 AS removed_last30_days
            """, new { since = SqliteTime.ToDb(DateTime.UtcNow.AddDays(-30)) }, cancellationToken: ct));
    }
}
