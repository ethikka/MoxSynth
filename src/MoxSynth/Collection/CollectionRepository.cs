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

    /// <summary>Owned copies per uuid with the foil/etched share (uuids without copies are omitted).</summary>
    public async Task<Dictionary<string, (int Total, int Foil)>> GetOwnedFinishCountsAsync(IEnumerable<string> cardUuids, CancellationToken ct = default)
    {
        var ids = cardUuids.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = Open();
        var rows = await conn.QueryAsync<(string CardUuid, long Total, long Foil)>(new CommandDefinition("""
            SELECT card_uuid, COUNT(*), SUM(CASE WHEN finish IN ('foil', 'etched') THEN 1 ELSE 0 END)
            FROM collection_cards
            WHERE removed_at IS NULL AND card_uuid IN @ids
            GROUP BY card_uuid
            """, new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.CardUuid, r => ((int)r.Total, (int)r.Foil));
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
        else if (!query.IncludeRemoved)
        {
            where.Add("cc.removed_at IS NULL");
        }
        var includeRemoved = query.AsOf is null && query.IncludeRemoved;

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

        // Prices (separate prices.db, attached as "pr"): price of the owned finish at the chosen provider.
        var usePrices = query.PriceProvider is not null && File.Exists(paths.PricesPath);
        const string priceJoin = "LEFT JOIN pr.prices px ON px.uuid = cc.card_uuid AND px.provider = @priceProvider AND px.finish = cc.finish";
        if (usePrices) p.Add("priceProvider", query.PriceProvider);

        // Sorting: explicit price sort → order:… from the Scryfall query (catalogue data, aliases c / s) → name.
        var join = scryfall.OrderBy is null ? "" : "LEFT JOIN mtg.cards c ON c.uuid = cc.card_uuid LEFT JOIN mtg.sets s ON s.code = c.setCode";
        if (usePrices) join += " " + priceJoin;
        const string byName = "MAX(cc.name) COLLATE NOCASE, MAX(cc.set_code), MAX(cc.number), cc.finish, cc.condition";
        var orderBy = (usePrices ? query.Sort : CollectionSort.Name) switch
        {
            CollectionSort.ValueDesc => $"COUNT(*) * MAX(px.price) IS NULL, COUNT(*) * MAX(px.price) DESC, {byName}",
            CollectionSort.PriceDesc => $"MAX(px.price) IS NULL, MAX(px.price) DESC, {byName}",
            CollectionSort.RisingDesc => $"MAX(px.price_7d) IS NULL, (MAX(px.price) - MAX(px.price_7d)) / MAX(px.price_7d) DESC, {byName}",
            CollectionSort.FallingDesc => $"MAX(px.price_7d) IS NULL, (MAX(px.price) - MAX(px.price_7d)) / MAX(px.price_7d) ASC, {byName}",
            _ when scryfall.OrderBy is not null => $"{scryfall.OrderBy}, MAX(cc.name) COLLATE NOCASE, cc.finish, cc.condition",
            _ => byName,
        };
        // Removed groups: right after the owned group of the same card when sorted by name, else after all owned ones.
        if (includeRemoved)
            orderBy = orderBy == byName ? $"{byName}, is_removed" : $"is_removed, {orderBy}";

        var whereSql = where.Count == 0 ? "1" : string.Join(" AND ", where); // empty with IncludeRemoved and no filters
        const string groupBy = "GROUP BY cc.card_uuid, cc.finish, cc.condition, cc.language, cc.removed_at IS NOT NULL";
        p.Add("take", pageSize);
        p.Add("skip", (page - 1) * pageSize);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        if (useCatalogue) await AttachAsync(conn, "mtg", paths.AllPrintingsPath, ct);
        if (usePrices) await AttachAsync(conn, "pr", paths.PricesPath, ct);
        try
        {
            // Copies = owned copies only; removed groups are counted separately.
            var totals = await conn.QuerySingleAsync<(long Groups, long Copies, long Removed)>(new CommandDefinition($"""
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN removed THEN 0 ELSE qty END), 0),
                       COALESCE(SUM(CASE WHEN removed THEN qty ELSE 0 END), 0)
                FROM (SELECT COUNT(*) AS qty, {(includeRemoved ? "cc.removed_at IS NOT NULL" : "0")} AS removed
                      FROM collection_cards cc WHERE {whereSql} {groupBy})
                """, p, cancellationToken: ct));

            // Estimated value of everything matching the filters (not just this page).
            (double? Value, string? Currency, long Unpriced) value = (null, null, 0);
            if (usePrices)
            {
                value = await conn.QuerySingleAsync<(double?, string?, long)>(new CommandDefinition($"""
                    SELECT SUM(px.price), MAX(px.currency), COALESCE(SUM(px.price IS NULL), 0)
                    FROM collection_cards cc
                    {priceJoin}
                    WHERE {whereSql}{(includeRemoved ? " AND cc.removed_at IS NULL" : "")}
                    """, p, cancellationToken: ct));
            }

            var groups = await conn.QueryAsync<CollectionGroup>(new CommandDefinition($"""
                SELECT cc.card_uuid, MAX(cc.name) AS name, MAX(cc.set_code) AS set_code, MAX(cc.number) AS number,
                       cc.finish, cc.condition, cc.language, COUNT(*) AS quantity,
                       MIN(cc.added_at) AS first_added_at, MAX(cc.added_at) AS last_added_at,
                       cc.removed_at IS NOT NULL AS is_removed, MAX(cc.removed_at) AS last_removed_at,
                       GROUP_CONCAT(DISTINCT cc.removal_reason) AS removal_reasons
                FROM collection_cards cc
                {join}
                WHERE {whereSql}
                {groupBy}
                ORDER BY {orderBy}
                LIMIT @take OFFSET @skip
                """, p, cancellationToken: ct));

            return new CollectionPage(
                new PagedResult<CollectionGroup>(groups.AsList(), (int)totals.Groups, page, pageSize),
                totals.Copies)
            {
                TotalValue = value.Value,
                Currency = value.Currency,
                UnpricedCopies = value.Unpriced,
                RemovedCopies = totals.Removed,
            };
        }
        finally
        {
            if (useCatalogue) await conn.ExecuteAsync("DETACH DATABASE mtg");
            if (usePrices) await conn.ExecuteAsync("DETACH DATABASE pr");
        }
    }

    /// <summary>Estimated value of the current collection at a price provider (dashboard).</summary>
    public async Task<CollectionPage> GetValueAsync(string provider, CancellationToken ct = default) =>
        await GetGroupsAsync(new CollectionQuery { PageSize = 1, PriceProvider = provider }, ct);

    /// <summary>Attaches another database (catalogue "mtg", prices "pr") to a collection connection, read-only use.</summary>
    private static async Task AttachAsync(SqliteConnection conn, string alias, string path, CancellationToken ct)
    {
        // Pooled connections may still have it attached if a previous query was interrupted.
        var attached = await conn.QueryAsync<string>(new CommandDefinition("SELECT name FROM pragma_database_list", cancellationToken: ct));
        if (!attached.Contains(alias))
            await conn.ExecuteAsync(new CommandDefinition($"ATTACH DATABASE @path AS {alias}", new { path }, cancellationToken: ct));
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
