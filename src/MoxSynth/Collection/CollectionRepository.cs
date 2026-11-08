using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Cards;
using MoxSynth.Infrastructure;
using MoxSynth.Profiles;

namespace MoxSynth.Collection;

/// <summary>Collection of the current profile (scoped per circuit – every profile has its own database).</summary>
public sealed class CollectionRepository(CurrentProfile currentProfile, ProfileStore profiles, CardRepository cards, DatabasePaths paths)
{
    private const string CardColumns = """
        id, card_uuid, name, set_code, number, finish, condition, language,
        purchase_price, notes, added_at, removed_at, removal_reason
        """;

    private SqliteConnection Open() => new(profiles.ConnectionStringOf(
        currentProfile.Value ?? throw new InvalidOperationException("No profile selected.")));

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
            where.Add("cc.added_at <= @asOf AND (cc.removed_at IS NULL OR cc.removed_at > @asOf)");
            p.Add("asOf", SqliteTime.ToDb(asOf));
        }
        else
        {
            where.Add("cc.removed_at IS NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            where.Add(@"cc.name LIKE @name ESCAPE '\'");
            p.Add("name", "%" + query.Name.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
        }
        if (!string.IsNullOrWhiteSpace(query.SetCode))
        {
            where.Add("cc.set_code = @setCode");
            p.Add("setCode", query.SetCode);
        }
        if (!string.IsNullOrWhiteSpace(query.Finish))
        {
            where.Add("cc.finish = @finish");
            p.Add("finish", query.Finish);
        }

        // Scryfall syntax: same SQL as the Search page, evaluated against the attached card catalogue for each owned
        // printing: p = the printing (by uuid), f = each of its faces (p itself + up to two other faces, all via the
        // uuid index), fs = its set. Only the cards in this collection are looked at, so it stays fast.
        // is:foil etc. refer to the owned copy (cc.finish).
        var scryfall = cards.IsAvailable ? cards.ParseScryfall(query.Scryfall, CardRepository.CollectionFinishColumn) : ScryfallFilter.Empty;
        var useCatalogue = scryfall.HasEffect;
        if (scryfall.WhereSql is not null)
        {
            where.Add($"""
                EXISTS (
                    SELECT 1
                    FROM mtg.cards p
                    JOIN mtg.cards f ON f.uuid IN (p.uuid, substr(p.otherFaceIds, 1, 36), substr(p.otherFaceIds, 39, 36))
                    LEFT JOIN mtg.sets fs ON fs.code = f.setCode
                    WHERE p.uuid = cc.card_uuid AND ({scryfall.WhereSql}))
                """);
            p.AddDynamicParams(scryfall.Parameters);
        }

        // order:… from the Scryfall query sorts by catalogue data (aliases c / s), otherwise by name.
        var join = scryfall.OrderBy is null ? "" : "LEFT JOIN mtg.cards c ON c.uuid = cc.card_uuid LEFT JOIN mtg.sets s ON s.code = c.setCode";
        var orderBy = scryfall.OrderBy is null
            ? "MAX(cc.name) COLLATE NOCASE, MAX(cc.set_code), MAX(cc.number), cc.finish, cc.condition"
            : $"{scryfall.OrderBy}, MAX(cc.name) COLLATE NOCASE, cc.finish, cc.condition";

        var whereSql = string.Join(" AND ", where);
        const string groupBy = "GROUP BY cc.card_uuid, cc.finish, cc.condition, cc.language";
        p.Add("take", pageSize);
        p.Add("skip", (page - 1) * pageSize);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        if (useCatalogue) await AttachCatalogueAsync(conn, ct);
        try
        {
            var totals = await conn.QuerySingleAsync<(long Groups, long Copies)>(new CommandDefinition($"""
                SELECT COUNT(*), COALESCE(SUM(qty), 0)
                FROM (SELECT COUNT(*) AS qty FROM collection_cards cc WHERE {whereSql} {groupBy})
                """, p, cancellationToken: ct));

            var groups = await conn.QueryAsync<CollectionGroup>(new CommandDefinition($"""
                SELECT cc.card_uuid, MAX(cc.name) AS name, MAX(cc.set_code) AS set_code, MAX(cc.number) AS number,
                       cc.finish, cc.condition, cc.language, COUNT(*) AS quantity,
                       MIN(cc.added_at) AS first_added_at, MAX(cc.added_at) AS last_added_at
                FROM collection_cards cc
                {join}
                WHERE {whereSql}
                {groupBy}
                ORDER BY {orderBy}
                LIMIT @take OFFSET @skip
                """, p, cancellationToken: ct));

            return new CollectionPage(
                new PagedResult<CollectionGroup>(groups.AsList(), (int)totals.Groups, page, pageSize),
                totals.Copies);
        }
        finally
        {
            if (useCatalogue) await conn.ExecuteAsync("DETACH DATABASE mtg");
        }
    }

    /// <summary>Attaches AllPrintings as schema "mtg" (read-only use) to a collection connection.</summary>
    private async Task AttachCatalogueAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Pooled connections may still have it attached if a previous query was interrupted.
        var attached = await conn.QueryAsync<string>(new CommandDefinition("SELECT name FROM pragma_database_list", cancellationToken: ct));
        if (!attached.Contains("mtg"))
            await conn.ExecuteAsync(new CommandDefinition("ATTACH DATABASE @path AS mtg", new { path = paths.AllPrintingsPath }, cancellationToken: ct));
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

    // ------------------------------------------------------------------ imports

    /// <summary>
    /// Adds every matched row of an import as individual cards in one transaction. Each card gets a note
    /// "Imported from {source} {date} · added there {original date}" (+ tags). With <paramref name="useOriginalDates"/>
    /// the original "date added" also becomes the card's added date (otherwise the import moment is used).
    /// </summary>
    public async Task<ImportRecord> ImportAsync(string source, ImportPreview preview, bool useOriginalDates, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var importDate = now.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var rows = preview.Matched.ToList();

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var importId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO imports (source, file_name, file_hash, imported_at, card_count)
            VALUES (@source, @fileName, @fileHash, @importedAt, @count);
            SELECT last_insert_rowid();
            """, new
        {
            source,
            fileName = preview.FileName,
            fileHash = preview.FileHash,
            importedAt = SqliteTime.ToDb(now),
            count = rows.Sum(r => r.Quantity),
        }, tx, cancellationToken: ct));

        foreach (var row in rows)
        {
            var card = row.Card!;
            var note = new StringBuilder($"Imported from {source} {importDate}");
            if (row.DateAdded is { } added)
                note.Append($" · added there {added.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrWhiteSpace(row.Tags)) note.Append($" · tags: {row.Tags}");

            var addedAt = useOriginalDates && row.DateAdded is { } original && original <= now ? original : now;
            var args = new
            {
                CardUuid = card.Uuid,
                card.Name,
                card.SetCode,
                card.Number,
                row.Finish,
                Condition = row.Condition.ToString(),
                row.Language,
                row.PurchasePrice,
                Notes = note.ToString(),
                AddedAt = SqliteTime.ToDb(addedAt),
                ImportId = importId,
            };
            for (var i = 0; i < row.Quantity; i++)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO collection_cards
                        (card_uuid, name, set_code, number, finish, condition, language, purchase_price, notes, added_at, import_id)
                    VALUES
                        (@CardUuid, @Name, @SetCode, @Number, @Finish, @Condition, @Language, @PurchasePrice, @Notes, @AddedAt, @ImportId)
                    """, args, tx, cancellationToken: ct));
            }
        }

        await tx.CommitAsync(ct);
        return (await GetImportsAsync(ct)).First(i => i.Id == importId);
    }

    public async Task<IReadOnlyList<ImportRecord>> GetImportsAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<ImportRecord>(new CommandDefinition(
            "SELECT id, source, file_name, file_hash, card_count, imported_at, undone_at FROM imports ORDER BY imported_at DESC",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Undoes an import: its cards are deleted outright (they were never really part of the collection, so no
    /// "removed" history is kept). Returns the number of cards deleted.
    /// </summary>
    public async Task<int> UndoImportAsync(long importId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM collection_cards WHERE import_id = @importId", new { importId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE imports SET undone_at = @now WHERE id = @importId AND undone_at IS NULL",
            new { importId, now = SqliteTime.ToDb(DateTime.UtcNow) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return deleted;
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
