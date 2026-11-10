using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Cards;
using MoxSynth.Infrastructure;
using MoxSynth.Prices;
using MoxSynth.Profiles;

namespace MoxSynth.Decks;

/// <summary>
/// Decklists of the current profile (scoped per circuit / request, like <c>CollectionRepository</c>), stored in the
/// profile's collection database (schema v3). A deck never changes the collection: ownership is only an indicator.
/// <para>Rules (as on Archidekt):</para>
/// <list type="bullet">
/// <item>A new deck gets Commander (counts; commander-style formats only), Sideboard and Maybeboard (don't count). These
/// keep their <see cref="DeckCategoryRoles">role</see> when renamed and can't be deleted.</item>
/// <item>A card added without a category goes to its type category (Creature &gt; Planeswalker &gt; Battle &gt; Instant &gt;
/// Sorcery &gt; Land &gt; Artifact &gt; Enchantment, else "Other"), created on demand (counts).</item>
/// <item>New categories are inserted after the last regular/Commander category, so Sideboard/Maybeboard stay at the end.</item>
/// <item>A card's first category is its primary one: it decides where the card is listed and whether it counts.</item>
/// <item>A deck card row is unique by (printing, finish, primary category): the same printing can be in the main deck
/// and in the sideboard as two rows. Adding to, moving or re-printing a row so that it meets its twin merges the two
/// (quantities add up, categories of the surviving row first).</item>
/// <item>Adding without a category bumps the first counting row of that printing+finish (as on Archidekt); import lines
/// without a category or section header behave the same.</item>
/// <item>Child rows are deleted explicitly in the same transaction (the code doesn't rely on <c>PRAGMA foreign_keys</c>).</item>
/// <item>Every change updates <c>decks.updated_at</c>.</item>
/// </list>
/// <para>Errors: <see cref="DeckNotFoundException"/> (unknown or deleted deck / card / category),
/// <see cref="ArgumentException"/> (invalid input), <see cref="InvalidOperationException"/> (rule violation, e.g. deleting
/// the Sideboard).</para>
/// </summary>
public sealed class DeckRepository(
    CurrentProfile currentProfile,
    ProfileStore profiles,
    CardRepository cards,
    CardIndex index,
    PriceRepository prices)
{
    /// <summary>
    /// Type category precedence: Creature &gt; Planeswalker &gt; Battle &gt; Instant &gt; Sorcery &gt; Artifact &gt; Enchantment,
    /// except that Land wins over Artifact and Enchantment (as on Archidekt): an Artifact Creature goes to "Creature",
    /// an Artifact Land (or Urza's Saga) to "Land", Dryad Arbor to "Creature".
    /// </summary>
    private static readonly string[] TypePrecedence =
        ["Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Land", "Artifact", "Enchantment"];

    private const string OtherTypeCategory = "Other";
    private const int MaxNameLength = 200;
    private const int MaxCategoryNameLength = 64;
    private const int MaxQuantity = 1000;

    private const string DeckColumns = "id, name, format, description, created_at, updated_at";
    private const string CategoryColumns = "id, deck_id, name, position, counts_in_deck, role";
    private const string DeckCardColumns = "id, deck_id, card_uuid, quantity, finish, added_at";

    private SqliteConnection Open() => new(profiles.ConnectionStringOf(
        currentProfile.Value ?? throw new InvalidOperationException("No profile selected.")));

    private static string Now() => SqliteTime.ToDb(DateTime.UtcNow);

    // ================================================================== formats

    /// <summary>Formats a deck can have: <c>casual</c> plus every MTGJSON legality key (cardLegalities columns).</summary>
    public async Task<IReadOnlyList<string>> GetFormatsAsync(CancellationToken ct = default)
    {
        var formats = new List<string> { DeckFormats.Casual };
        formats.AddRange(await LegalityColumnsAsync(ct));
        return formats;
    }

    private async Task<IReadOnlyList<string>> LegalityColumnsAsync(CancellationToken ct)
    {
        if (!cards.IsAvailable) return [];
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = cards.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        var columns = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM pragma_table_info('cardLegalities') WHERE name <> 'uuid' ORDER BY cid", cancellationToken: ct));
        return columns.AsList();
    }

    private async Task<string> NormalizeFormatAsync(string format, CancellationToken ct)
    {
        var f = (format ?? "").Trim().ToLowerInvariant();
        if (f == DeckFormats.Casual) return f;
        var known = await LegalityColumnsAsync(ct);
        if (known.Count > 0 ? known.Contains(f) : f.Length > 0 && f.All(char.IsAsciiLetterLower)) return f;
        throw new ArgumentException($"Unknown format “{format}”.", nameof(format));
    }

    // ================================================================== reads

    /// <summary>All decks that aren't deleted, most recently updated first. <paramref name="provider"/> null = no prices.</summary>
    public async Task<IReadOnlyList<DeckSummary>> ListAsync(string? provider, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        var decks = (await conn.QueryAsync<DeckRow>(new CommandDefinition(
            $"SELECT {DeckColumns} FROM decks WHERE deleted_at IS NULL ORDER BY updated_at DESC, id DESC",
            cancellationToken: ct))).AsList();
        var details = await LoadDetailsAsync(conn, decks, provider, ct);
        return details.Select(d => new DeckSummary
        {
            Id = d.Id,
            Name = d.Name,
            Format = d.Format,
            CardCount = d.CardCount,
            CommanderNames = d.CommanderNames,
            CoverImageUrl = d.CoverImageUrl,
            UpdatedAt = d.UpdatedAt,
            Value = d.Value,
            Currency = d.Currency,
            IsLegal = d.Legality.IsLegal,
        }).ToList();
    }

    /// <summary>One deck with categories, cards, legality, value and ownership. <paramref name="provider"/> null = no prices.</summary>
    public async Task<DeckDetail> GetAsync(long deckId, string? provider, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        var deck = await RequireDeckAsync(conn, null, deckId, ct);
        return (await LoadDetailsAsync(conn, [deck], provider, ct))[0];
    }

    /// <summary>The deck as text (see <see cref="DeckText.Format"/>).</summary>
    public async Task<string> ExportTextAsync(long deckId, CancellationToken ct = default) =>
        DeckText.Format(await GetAsync(deckId, null, ct));

    private async Task<List<DeckDetail>> LoadDetailsAsync(SqliteConnection conn, IReadOnlyList<DeckRow> decks, string? provider,
        CancellationToken ct)
    {
        if (decks.Count == 0) return [];
        var ids = decks.Select(d => d.Id).ToArray();

        var categories = (await conn.QueryAsync<CategoryRow>(new CommandDefinition(
            $"SELECT {CategoryColumns} FROM deck_categories WHERE deck_id IN @ids ORDER BY deck_id, position, id",
            new { ids }, cancellationToken: ct))).AsList();
        var deckCards = (await conn.QueryAsync<DeckCardRow>(new CommandDefinition(
            $"SELECT {DeckCardColumns} FROM deck_cards WHERE deck_id IN @ids ORDER BY added_at, id",
            new { ids }, cancellationToken: ct))).AsList();
        var links = (await conn.QueryAsync<CardCategoryRow>(new CommandDefinition("""
            SELECT l.deck_card_id, l.category_id, l.position
            FROM deck_card_categories l
            JOIN deck_cards dc ON dc.id = l.deck_card_id
            WHERE dc.deck_id IN @ids
            ORDER BY l.deck_card_id, l.position, l.category_id
            """, new { ids }, cancellationToken: ct))).AsList();

        var uuids = deckCards.Select(c => c.CardUuid).Distinct().ToList();
        var facts = await LoadFactsAsync(uuids, ct);

        var priceMap = provider is not null && prices.IsAvailable
            ? await prices.GetAsync(uuids, provider, ct)
            : new Dictionary<string, Dictionary<string, PriceInfo>>();

        // Ownership (collection_cards in the same database; owned = not removed).
        var ownedExact = new Dictionary<(string, string), int>();
        foreach (var chunk in uuids.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string CardUuid, string Finish, long Count)>(new CommandDefinition("""
                SELECT card_uuid, finish, COUNT(*) FROM collection_cards
                WHERE removed_at IS NULL AND card_uuid IN @chunk
                GROUP BY card_uuid, finish
                """, new { chunk }, cancellationToken: ct));
            foreach (var r in rows) ownedExact[(r.CardUuid, r.Finish)] = (int)r.Count;
        }
        var names = facts.Values.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ownedByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in names.Chunk(500))
        {
            var rows = await conn.QueryAsync<(string Name, long Count)>(new CommandDefinition("""
                SELECT MAX(name), COUNT(*) FROM collection_cards
                WHERE removed_at IS NULL AND name COLLATE NOCASE IN @chunk
                GROUP BY name COLLATE NOCASE
                """, new { chunk }, cancellationToken: ct));
            foreach (var r in rows) ownedByName[r.Name] = (int)r.Count;
        }

        var categoriesByDeck = categories.ToLookup(c => c.DeckId);
        var cardsByDeck = deckCards.ToLookup(c => c.DeckId);
        var linksByCard = links.ToLookup(l => l.DeckCardId);

        var result = new List<DeckDetail>(decks.Count);
        foreach (var deck in decks)
        {
            var deckCategories = categoriesByDeck[deck.Id].ToList();
            var categoryById = deckCategories.ToDictionary(c => c.Id);
            var commanderStyle = DeckFormats.IsCommanderStyle(deck.Format);

            var views = new List<(DeckCardView View, CategoryRow? Primary, bool Counts, DeckCardFacts? Facts, string AddedAt)>();
            foreach (var row in cardsByDeck[deck.Id])
            {
                var categoryIds = linksByCard[row.Id].Select(l => l.CategoryId).Where(categoryById.ContainsKey).ToList();
                var primary = categoryIds.Count > 0 ? categoryById[categoryIds[0]] : null;
                var f = facts.GetValueOrDefault(row.CardUuid);
                var price = priceMap.GetValueOrDefault(row.CardUuid)?.GetValueOrDefault(row.Finish);
                var view = new DeckCardView
                {
                    DeckCardId = row.Id,
                    CardUuid = row.CardUuid,
                    Name = f?.Name ?? row.CardUuid,
                    SetCode = f?.SetCode ?? "",
                    Number = f?.Number ?? "",
                    Finish = row.Finish,
                    Quantity = row.Quantity,
                    CategoryIds = categoryIds,
                    ManaCost = f?.ManaCost,
                    ManaValue = f?.ManaValue ?? 0,
                    Colors = f?.ColorList ?? [],
                    ColorIdentity = f?.ColorIdentityList ?? [],
                    TypeLine = f?.Type,
                    Types = f?.TypeList ?? [],
                    Rarity = f?.Rarity,
                    ImageUrl = ScryfallImages.Url(f?.ScryfallId),
                    SmallImageUrl = ScryfallImages.Url(f?.ScryfallId, "small"),
                    Price = price?.Price,
                    OwnedExact = ownedExact.GetValueOrDefault((row.CardUuid, row.Finish)),
                    OwnedAny = f is null ? ownedExact.GetValueOrDefault((row.CardUuid, row.Finish)) : ownedByName.GetValueOrDefault(f.Name),
                };
                views.Add((view, primary, primary?.CountsInDeck ?? true, f, row.AddedAt));
            }

            // Legality.
            var legality = DeckLegality.Evaluate(deck.Format, views.Select(v => new DeckLegalityCard(
                v.View.DeckCardId,
                v.View.Name,
                v.View.Quantity,
                BoardOf(v.Primary, v.Counts, commanderStyle),
                v.Facts?.Legalities.GetValueOrDefault(deck.Format),
                v.Facts?.IsBasicLand ?? false,
                DeckLegality.DeckLimitFromText(v.Facts?.Text, v.Facts?.HasAlternativeDeckLimit == 1),
                v.View.ColorIdentity,
                v.Facts is not null && DeckLegality.CanBeCommander(deck.Format, v.Facts.LeadershipSkills, v.Facts.IsLegendaryCreature),
                v.Facts is not null)).ToList());

            var cardViews = views
                .OrderBy(v => v.Primary?.Position ?? int.MaxValue)
                .ThenBy(v => v.View.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.View.SetCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.View.Number, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.View.Finish)
                .Select(v => (v.View with { IllegalReason = legality.CardReasons.GetValueOrDefault(v.View.DeckCardId) }, v.Primary, v.Counts, v.AddedAt))
                .ToList();

            var counted = cardViews.Where(v => v.Counts).Select(v => v.Item1).ToList();
            var priced = counted.Where(c => c.Price is not null).ToList();
            var currency = counted.Select(c => priceMap.GetValueOrDefault(c.CardUuid)?.GetValueOrDefault(c.Finish)?.Currency)
                .FirstOrDefault(c => c is not null);

            var commanders = commanderStyle
                ? cardViews.Where(v => v.Primary?.Role == DeckCategoryRoles.Commander)
                    .OrderBy(v => v.AddedAt, StringComparer.Ordinal).ThenBy(v => v.Item1.DeckCardId)
                    .Select(v => v.Item1).ToList()
                : [];
            var cover = commanders.FirstOrDefault(c => c.ImageUrl is not null)?.ImageUrl
                        ?? counted.Where(c => c.Price is not null && c.ImageUrl is not null).MaxBy(c => c.Price)?.ImageUrl
                        ?? counted.FirstOrDefault(c => c.ImageUrl is not null)?.ImageUrl;

            result.Add(new DeckDetail
            {
                Id = deck.Id,
                Name = deck.Name,
                Format = deck.Format,
                Description = deck.Description,
                CreatedAt = deck.CreatedAt,
                UpdatedAt = deck.UpdatedAt,
                Categories = deckCategories.Select(c => new DeckCategoryView
                {
                    Id = c.Id,
                    Name = c.Name,
                    Position = c.Position,
                    CountsInDeck = c.CountsInDeck,
                    Role = c.Role,
                    CardCount = cardViews.Where(v => v.Primary?.Id == c.Id).Sum(v => v.Item1.Quantity),
                }).ToList(),
                Cards = cardViews.Select(v => v.Item1).ToList(),
                Legality = legality,
                Value = priced.Count > 0 ? Math.Round(priced.Sum(c => c.Price!.Value * c.Quantity), 2) : null,
                Currency = currency,
                CardCount = counted.Sum(c => c.Quantity),
                OwnedCount = counted.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Sum(g => Math.Min(g.Sum(c => c.Quantity), g.First().OwnedAny)),
                CommanderNames = commanders.Select(c => c.Name).ToList(),
                CoverImageUrl = cover,
            });
        }
        return result;
    }

    private static DeckBoard BoardOf(CategoryRow? primary, bool counts, bool commanderStyle) => primary?.Role switch
    {
        DeckCategoryRoles.Commander when commanderStyle => DeckBoard.Commander,
        DeckCategoryRoles.Sideboard => DeckBoard.Sideboard,
        DeckCategoryRoles.Maybeboard => DeckBoard.Maybeboard,
        _ => counts ? DeckBoard.Main : DeckBoard.Excluded,
    };

    /// <summary>Catalogue facts (primary face rows) plus all legalities for the given printings.</summary>
    private async Task<Dictionary<string, DeckCardFacts>> LoadFactsAsync(IReadOnlyCollection<string> uuids, CancellationToken ct)
    {
        var result = new Dictionary<string, DeckCardFacts>();
        if (uuids.Count == 0 || !cards.IsAvailable) return result;

        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = cards.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        foreach (var chunk in uuids.Distinct().Chunk(500))
        {
            var rows = await conn.QueryAsync<DeckCardFacts>(new CommandDefinition("""
                SELECT c.uuid, c.name, c.faceName, c.side, c.setCode, c.number, c.manaCost, c.manaValue, c.colors,
                       c.colorIdentity, c.type, c.types, c.supertypes, c.rarity, c.text, c.leadershipSkills,
                       c.hasAlternativeDeckLimit, c.finishes, c.otherFaceIds, ci.scryfallId
                FROM cards c
                LEFT JOIN cardIdentifiers ci ON ci.uuid = c.uuid
                WHERE c.uuid IN @chunk
                """, new { chunk }, cancellationToken: ct));
            foreach (var r in rows) result.TryAdd(r.Uuid, r);

            var legalities = await conn.QueryAsync(new CommandDefinition(
                "SELECT * FROM cardLegalities WHERE uuid IN @chunk", new { chunk }, cancellationToken: ct));
            foreach (IDictionary<string, object?> row in legalities)
            {
                if (row["uuid"] is not string uuid || !result.TryGetValue(uuid, out var f)) continue;
                foreach (var (column, value) in row)
                    if (column != "uuid") f.Legalities[column] = value as string;
            }
        }
        return result;
    }

    /// <summary>Facts of one printing; a back-face uuid is mapped to the front face (the physical card).</summary>
    private async Task<DeckCardFacts> RequireCardAsync(string cardUuid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardUuid)) throw new ArgumentException("No card given.", nameof(cardUuid));
        var facts = await LoadFactsAsync([cardUuid.Trim()], ct);
        if (!facts.TryGetValue(cardUuid.Trim(), out var card))
            throw new ArgumentException($"Unknown card “{cardUuid}”.", nameof(cardUuid));
        if (card.Side is null or "a") return card;

        var front = index.BySetAndNumber(card.SetCode, card.Number);
        if (front is not null && (await LoadFactsAsync([front.Uuid], ct)).TryGetValue(front.Uuid, out var frontFacts))
            return frontFacts;
        return card;
    }

    // ================================================================== decks

    /// <summary>Creates a deck with its built-in categories; returns its id.</summary>
    public async Task<long> CreateAsync(string name, string format, string? description = null, CancellationToken ct = default)
    {
        var deckName = ValidateDeckName(name);
        var deckFormat = await NormalizeFormatAsync(format, ct);
        var now = Now();

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deckId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO decks (name, format, description, created_at, updated_at)
            VALUES (@deckName, @deckFormat, @description, @now, @now);
            SELECT last_insert_rowid();
            """, new { deckName, deckFormat, description = CleanDescription(description), now }, tx, cancellationToken: ct));

        var position = 0;
        if (DeckFormats.IsCommanderStyle(deckFormat))
            await InsertCategoryRowAsync(conn, tx, deckId, "Commander", position++, true, DeckCategoryRoles.Commander, ct);
        await InsertCategoryRowAsync(conn, tx, deckId, "Sideboard", position++, false, DeckCategoryRoles.Sideboard, ct);
        await InsertCategoryRowAsync(conn, tx, deckId, "Maybeboard", position, false, DeckCategoryRoles.Maybeboard, ct);

        await tx.CommitAsync(ct);
        return deckId;
    }

    /// <summary>
    /// Changes name, format and description. Switching to a commander-style format adds the Commander category if the
    /// deck has none (an existing category named "Commander" gets the role). Switching away keeps it (it then acts as a
    /// regular category).
    /// </summary>
    public async Task UpdateAsync(long deckId, string name, string format, string? description = null, CancellationToken ct = default)
    {
        var deckName = ValidateDeckName(name);
        var deckFormat = await NormalizeFormatAsync(format, ct);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE decks SET name = @deckName, format = @deckFormat, description = @description, updated_at = @now
            WHERE id = @deckId
            """, new { deckId, deckName, deckFormat, description = CleanDescription(description), now = Now() }, tx, cancellationToken: ct));

        if (DeckFormats.IsCommanderStyle(deckFormat))
        {
            var categories = await CategoriesAsync(conn, tx, deckId, ct);
            if (categories.All(c => c.Role != DeckCategoryRoles.Commander))
            {
                if (categories.FirstOrDefault(c => c.Name.Equals("Commander", StringComparison.OrdinalIgnoreCase)) is { } existing)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE deck_categories SET role = @role WHERE id = @id",
                        new { role = DeckCategoryRoles.Commander, id = existing.Id }, tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE deck_categories SET position = position + 1 WHERE deck_id = @deckId",
                        new { deckId }, tx, cancellationToken: ct));
                    await InsertCategoryRowAsync(conn, tx, deckId, "Commander", 0, true, DeckCategoryRoles.Commander, ct);
                }
            }
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Soft delete: the deck disappears from lists and can no longer be read or changed.</summary>
    public async Task DeleteAsync(long deckId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);
        var now = Now();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE decks SET deleted_at = @now, updated_at = @now WHERE id = @deckId", new { deckId, now }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Copies a deck (categories, roles, cards) as "{name} (copy)"; returns the new deck's id.</summary>
    public async Task<long> DuplicateAsync(long deckId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var deck = await RequireDeckAsync(conn, tx, deckId, ct);
        var now = Now();

        var copyName = deck.Name + " (copy)";
        if (copyName.Length > MaxNameLength) copyName = copyName[^MaxNameLength..];
        var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO decks (name, format, description, created_at, updated_at)
            VALUES (@copyName, @Format, @Description, @now, @now);
            SELECT last_insert_rowid();
            """, new { copyName, deck.Format, deck.Description, now }, tx, cancellationToken: ct));

        var categoryMap = new Dictionary<long, long>();
        foreach (var c in await CategoriesAsync(conn, tx, deckId, ct))
            categoryMap[c.Id] = await InsertCategoryRowAsync(conn, tx, newId, c.Name, c.Position, c.CountsInDeck, c.Role, ct);

        var rows = await conn.QueryAsync<DeckCardRow>(new CommandDefinition(
            $"SELECT {DeckCardColumns} FROM deck_cards WHERE deck_id = @deckId ORDER BY id", new { deckId }, tx, cancellationToken: ct));
        foreach (var row in rows)
        {
            var newCardId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO deck_cards (deck_id, card_uuid, quantity, finish, added_at)
                VALUES (@newId, @CardUuid, @Quantity, @Finish, @AddedAt);
                SELECT last_insert_rowid();
                """, new { newId, row.CardUuid, row.Quantity, row.Finish, row.AddedAt }, tx, cancellationToken: ct));
            var links = await conn.QueryAsync<CardCategoryRow>(new CommandDefinition(
                "SELECT deck_card_id, category_id, position FROM deck_card_categories WHERE deck_card_id = @id",
                new { id = row.Id }, tx, cancellationToken: ct));
            foreach (var link in links.Where(l => categoryMap.ContainsKey(l.CategoryId)))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO deck_card_categories (deck_card_id, category_id, position) VALUES (@newCardId, @categoryId, @Position)",
                    new { newCardId, categoryId = categoryMap[link.CategoryId], link.Position }, tx, cancellationToken: ct));
            }
        }

        await tx.CommitAsync(ct);
        return newId;
    }

    // ================================================================== cards

    /// <summary>
    /// Adds a printing to <paramref name="categoryName"/> (found case-insensitively; "Sideboard"/"Maybeboard"/"Commander"
    /// also find the renamed built-in category; else created). When a row with the same printing and finish already has
    /// that category as its primary one, its quantity goes up instead (its other categories stay). Without a category
    /// ("auto"), the first row of that printing+finish (by category position) whose primary category counts is bumped,
    /// else the card goes to its type category. Returns the deck card id.
    /// </summary>
    public async Task<long> AddCardAsync(long deckId, string cardUuid, int quantity = 1, string finish = CardFinishes.NonFoil,
        string? categoryName = null, CancellationToken ct = default)
    {
        if (quantity is < 1 or > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), $"Quantity must be between 1 and {MaxQuantity}.");
        var card = await RequireCardAsync(cardUuid, ct);
        var cardFinish = ValidateFinish(card, finish);
        var category = categoryName is null ? null : ValidateCategoryName(categoryName);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);

        var id = await AddOrIncrementAsync(conn, tx, deckId, card, cardFinish, quantity,
            category is null ? [] : [new DeckTextCategory(category)], ct);
        await TouchAsync(conn, tx, deckId, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Sets the quantity; 0 removes the card from the deck.</summary>
    public async Task SetQuantityAsync(long deckCardId, int quantity, CancellationToken ct = default)
    {
        if (quantity is < 0 or > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), $"Quantity must be between 0 and {MaxQuantity}.");

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await RequireDeckCardAsync(conn, tx, deckCardId, ct);

        if (quantity == 0)
            await DeleteDeckCardAsync(conn, tx, deckCardId, ct);
        else
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE deck_cards SET quantity = @quantity WHERE id = @deckCardId", new { deckCardId, quantity }, tx, cancellationToken: ct));

        await TouchAsync(conn, tx, row.DeckId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Replaces the card's categories (the first is primary). Missing categories are created (counting); names are
    /// matched like in <see cref="AddCardAsync"/>. An empty list puts the card back in its type category. When another row
    /// of the same printing and finish already has the new primary category, the two merge into that row. Returns the
    /// resulting deck card id (the other row's id after a merge).
    /// </summary>
    public async Task<long> SetCategoriesAsync(long deckCardId, IReadOnlyList<string> categoryNames, CancellationToken ct = default)
    {
        var names = categoryNames.Select(ValidateCategoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await RequireDeckCardAsync(conn, tx, deckCardId, ct);

        var categoryIds = new List<long>();
        foreach (var name in names)
            categoryIds.Add(await ResolveCategoryAsync(conn, tx, row.DeckId, new DeckTextCategory(name), ct));
        if (categoryIds.Count == 0)
        {
            var facts = (await LoadFactsAsync([row.CardUuid], ct)).GetValueOrDefault(row.CardUuid);
            categoryIds.Add(await ResolveCategoryAsync(conn, tx, row.DeckId, new DeckTextCategory(TypeCategoryOf(facts)), ct));
        }

        await SetLinksAsync(conn, tx, deckCardId, categoryIds.Distinct().ToList(), ct);
        var resultId = await MergeIntoTwinAsync(conn, tx, deckCardId, ct);
        await TouchAsync(conn, tx, row.DeckId, ct);
        await tx.CommitAsync(ct);
        return resultId;
    }

    /// <summary>
    /// Switches the card to another printing of the same card name. <paramref name="finish"/> null keeps the current
    /// finish when the new printing has it, else its first finish. If the deck already has that printing+finish with the
    /// same primary category, the two entries merge (quantities add up; categories of the existing entry first).
    /// Returns the resulting deck card id.
    /// </summary>
    public async Task<long> ChangePrintingAsync(long deckCardId, string newCardUuid, string? finish = null, CancellationToken ct = default)
    {
        var card = await RequireCardAsync(newCardUuid, ct);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await RequireDeckCardAsync(conn, tx, deckCardId, ct);

        var current = (await LoadFactsAsync([row.CardUuid], ct)).GetValueOrDefault(row.CardUuid);
        if (current is not null && !current.Name.Equals(card.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"“{card.Name}” is a different card than “{current.Name}”.", nameof(newCardUuid));

        var newFinish = finish is null
            ? card.FinishList.Contains(row.Finish) ? row.Finish : card.FinishList[0]
            : ValidateFinish(card, finish);
        if (card.Uuid == row.CardUuid && newFinish == row.Finish) return row.Id;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE deck_cards SET card_uuid = @uuid, finish = @newFinish WHERE id = @id",
            new { uuid = card.Uuid, newFinish, id = row.Id }, tx, cancellationToken: ct));
        var resultId = await MergeIntoTwinAsync(conn, tx, row.Id, ct);

        await TouchAsync(conn, tx, row.DeckId, ct);
        await tx.CommitAsync(ct);
        return resultId;
    }

    // ================================================================== categories

    /// <summary>Adds an empty category (after the last regular category); returns its id.</summary>
    public async Task<long> AddCategoryAsync(long deckId, string name, bool countsInDeck = true, CancellationToken ct = default)
    {
        var categoryName = ValidateCategoryName(name);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);
        if ((await CategoriesAsync(conn, tx, deckId, ct)).Any(c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The deck already has a category “{categoryName}”.", nameof(name));

        var id = await InsertRegularCategoryAsync(conn, tx, deckId, categoryName, countsInDeck, ct);
        await TouchAsync(conn, tx, deckId, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Renames a category (the built-in ones too; their role stays).</summary>
    public async Task RenameCategoryAsync(long categoryId, string newName, CancellationToken ct = default)
    {
        var categoryName = ValidateCategoryName(newName);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var category = await RequireCategoryAsync(conn, tx, categoryId, ct);
        if ((await CategoriesAsync(conn, tx, category.DeckId, ct))
            .Any(c => c.Id != categoryId && c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The deck already has a category “{categoryName}”.", nameof(newName));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE deck_categories SET name = @categoryName WHERE id = @categoryId", new { categoryId, categoryName }, tx, cancellationToken: ct));
        await TouchAsync(conn, tx, category.DeckId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Deletes a category. Its cards move to their next category; cards without another category go back to their type
    /// category (re-created when it is the one being deleted). Commander, Sideboard and Maybeboard can't be deleted.
    /// </summary>
    public async Task DeleteCategoryAsync(long categoryId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var category = await RequireCategoryAsync(conn, tx, categoryId, ct);
        if (category.Role is not null)
            throw new InvalidOperationException($"“{category.Name}” is a built-in category and can't be deleted.");

        var cardsInCategory = (await conn.QueryAsync<DeckCardRow>(new CommandDefinition($"""
            SELECT {DeckCardColumns} FROM deck_cards
            WHERE id IN (SELECT deck_card_id FROM deck_card_categories WHERE category_id = @categoryId)
            """, new { categoryId }, tx, cancellationToken: ct))).AsList();

        var orphans = new List<DeckCardRow>();
        foreach (var card in cardsInCategory)
        {
            var remaining = (await CardCategoryIdsAsync(conn, tx, card.Id, ct)).Where(id => id != categoryId).ToList();
            if (remaining.Count > 0) await SetLinksAsync(conn, tx, card.Id, remaining, ct);
            else
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM deck_card_categories WHERE deck_card_id = @id", new { id = card.Id }, tx, cancellationToken: ct));
                orphans.Add(card);
            }
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM deck_card_categories WHERE category_id = @categoryId;
            DELETE FROM deck_categories WHERE id = @categoryId;
            """, new { categoryId }, tx, cancellationToken: ct));
        await RenumberCategoriesAsync(conn, tx, category.DeckId, null, ct);

        if (orphans.Count > 0)
        {
            var facts = await LoadFactsAsync(orphans.Select(o => o.CardUuid).ToList(), ct);
            foreach (var orphan in orphans)
            {
                var typeCategory = await ResolveCategoryAsync(conn, tx, category.DeckId,
                    new DeckTextCategory(TypeCategoryOf(facts.GetValueOrDefault(orphan.CardUuid))), ct);
                await SetLinksAsync(conn, tx, orphan.Id, [typeCategory], ct);
            }
        }

        // Cards whose primary category changed may now meet a twin (same printing, finish and primary category).
        foreach (var card in cardsInCategory)
            await MergeIntoTwinAsync(conn, tx, card.Id, ct);

        await TouchAsync(conn, tx, category.DeckId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Moves a category to <paramref name="newPosition"/> (0-based, clamped); the others shift.</summary>
    public async Task MoveCategoryAsync(long categoryId, int newPosition, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var category = await RequireCategoryAsync(conn, tx, categoryId, ct);
        await RenumberCategoriesAsync(conn, tx, category.DeckId, (categoryId, newPosition), ct);
        await TouchAsync(conn, tx, category.DeckId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Whether cards listed in this category count towards the deck (card count, value, legality).</summary>
    public async Task SetCategoryCountsAsync(long categoryId, bool countsInDeck, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var category = await RequireCategoryAsync(conn, tx, categoryId, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE deck_categories SET counts_in_deck = @countsInDeck WHERE id = @categoryId",
            new { categoryId, countsInDeck }, tx, cancellationToken: ct));
        await TouchAsync(conn, tx, category.DeckId, ct);
        await tx.CommitAsync(ct);
    }

    // ================================================================== text import

    /// <summary>
    /// Imports a text decklist (see <see cref="DeckText"/>). With <paramref name="replace"/> all cards and all non-built-in
    /// categories are removed first. A line adds to an existing row only when printing, finish and primary category match
    /// (so "2 main + 2 sideboard" stays two rows). Unmatched lines are skipped and reported.
    /// </summary>
    public async Task<DeckImportResult> ImportTextAsync(long deckId, string text, bool replace, CancellationToken ct = default)
    {
        var parsed = DeckText.Parse(text);
        var unmatched = new List<DeckImportLine>(parsed.Errors);
        var warnings = new List<DeckImportLine>();

        var matches = new List<DeckTextMatch>();
        foreach (var line in parsed.Lines)
        {
            var match = DeckText.Resolve(line, index);
            if (match.Card is null) unmatched.Add(new DeckImportLine(line.LineNumber, line.Text, match.Problem ?? "No match."));
            else
            {
                matches.Add(match);
                if (match.Warning is not null) warnings.Add(new DeckImportLine(line.LineNumber, line.Text, match.Warning));
            }
        }
        var facts = await LoadFactsAsync(matches.Select(m => m.Card!.Uuid).ToList(), ct);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);

        if (replace) await ClearDeckAsync(conn, tx, deckId, ct);

        var added = 0;
        foreach (var match in matches)
        {
            if (!facts.TryGetValue(match.Card!.Uuid, out var card))
            {
                unmatched.Add(new DeckImportLine(match.Line.LineNumber, match.Line.Text, "Card not found in the card database."));
                continue;
            }
            var categories = match.Line.Categories
                .Select(c => c with { Name = SanitizeCategoryName(c.Name) })
                .Where(c => c.Name.Length > 0)
                .ToList();

            await AddOrIncrementAsync(conn, tx, deckId, card, match.Finish, match.Line.Quantity, categories, ct);
            added += match.Line.Quantity;
        }

        await TouchAsync(conn, tx, deckId, ct);
        await tx.CommitAsync(ct);

        return new DeckImportResult
        {
            Added = added,
            Unmatched = unmatched.OrderBy(u => u.Line).ToList(),
            Warnings = warnings.OrderBy(w => w.Line).ToList(),
        };
    }

    /// <summary>
    /// Imports already-resolved cards (e.g. from an Archidekt deck) in one transaction. With <paramref name="replace"/>
    /// all cards and all non-built-in categories are removed first (as in <see cref="ImportTextAsync"/>).
    /// <paramref name="categories"/> are ensured first, in the given order: a missing one is created with its
    /// <see cref="DeckTextCategory.CountsInDeck"/>, an existing regular one gets that setting, a built-in one (matched by
    /// name or role, e.g. "Sideboard", "SB", "Commander") keeps its own. Cards are then added like text import lines (a row
    /// of the same printing, finish and primary category is incremented). Cards that aren't in the card database are
    /// reported as unmatched; a finish the printing doesn't have falls back to its first finish with a warning.
    /// </summary>
    public async Task<DeckImportResult> ImportCardsAsync(long deckId, IReadOnlyList<DeckTextCategory> categories,
        IReadOnlyList<DeckImportCard> cards, bool replace, CancellationToken ct = default)
    {
        var unmatched = new List<DeckImportLine>();
        var warnings = new List<DeckImportLine>();
        var facts = await LoadFactsAsync(cards.Select(c => c.CardUuid).Distinct().ToList(), ct);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RequireDeckAsync(conn, tx, deckId, ct);

        if (replace) await ClearDeckAsync(conn, tx, deckId, ct);

        var counts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            var name = SanitizeCategoryName(category.Name);
            if (name.Length == 0 || !counts.TryAdd(name, category.CountsInDeck)) continue;
            if (await FindCategoryAsync(conn, tx, deckId, name, ct) is not { } id)
            {
                await InsertRegularCategoryAsync(conn, tx, deckId, name, category.CountsInDeck, ct);
                continue;
            }
            var existing = (await CategoriesAsync(conn, tx, deckId, ct)).First(c => c.Id == id);
            if (existing.Role is null && existing.CountsInDeck != category.CountsInDeck)
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE deck_categories SET counts_in_deck = @counts WHERE id = @id",
                    new { counts = category.CountsInDeck, id }, tx, cancellationToken: ct));
        }

        var added = 0;
        foreach (var item in cards)
        {
            if (!facts.TryGetValue(item.CardUuid, out var card))
            {
                unmatched.Add(new DeckImportLine(item.Line, item.Text, "Card not found in the card database."));
                continue;
            }
            var quantity = Math.Clamp(item.Quantity, 1, MaxQuantity);
            var finish = (item.Finish ?? "").Trim().ToLowerInvariant();
            if (!card.FinishList.Contains(finish))
            {
                warnings.Add(new DeckImportLine(item.Line, item.Text,
                    $"This printing doesn't exist in {CardFinishes.Label(finish).ToLowerInvariant()}; imported as {CardFinishes.Label(card.FinishList[0]).ToLowerInvariant()}."));
                finish = card.FinishList[0];
            }
            var cardCategories = item.Categories
                .Select(SanitizeCategoryName)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => new DeckTextCategory(n, counts.GetValueOrDefault(n, true)))
                .ToList();

            await AddOrIncrementAsync(conn, tx, deckId, card, finish, quantity, cardCategories, ct);
            added += quantity;
        }

        await TouchAsync(conn, tx, deckId, ct);
        await tx.CommitAsync(ct);

        return new DeckImportResult
        {
            Added = added,
            Unmatched = unmatched.OrderBy(u => u.Line).ToList(),
            Warnings = warnings.OrderBy(w => w.Line).ToList(),
        };
    }

    // ================================================================== helpers

    /// <summary>Removes all cards and all non-built-in categories of a deck (the replace step of the imports).</summary>
    private static async Task ClearDeckAsync(DbConnection conn, DbTransaction tx, long deckId, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM deck_card_categories WHERE deck_card_id IN (SELECT id FROM deck_cards WHERE deck_id = @deckId);
            DELETE FROM deck_cards WHERE deck_id = @deckId;
            DELETE FROM deck_categories WHERE deck_id = @deckId AND role IS NULL;
            """, new { deckId }, tx, cancellationToken: ct));
        await RenumberCategoriesAsync(conn, tx, deckId, null, ct);
    }

    /// <summary>
    /// Adds a new row, or increments the row with the same printing, finish and primary category (the first of
    /// <paramref name="categories"/>). Without categories ("auto"), the first row of that printing+finish (by category
    /// position) whose primary category counts is incremented; only when there is none does the type category apply.
    /// Returns the row id.
    /// </summary>
    private async Task<long> AddOrIncrementAsync(DbConnection conn, DbTransaction tx,
        long deckId, DeckCardFacts card, string finish, int quantity, IReadOnlyList<DeckTextCategory> categories, CancellationToken ct)
    {
        if (categories.Count == 0 && await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
                SELECT dc.id FROM deck_cards dc
                JOIN deck_categories k ON k.id = (SELECT l.category_id FROM deck_card_categories l WHERE l.deck_card_id = dc.id
                                                  ORDER BY l.position, l.category_id LIMIT 1)
                WHERE dc.deck_id = @deckId AND dc.card_uuid = @uuid AND dc.finish = @finish AND k.counts_in_deck = 1
                ORDER BY k.position, dc.id
                LIMIT 1
                """, new { deckId, uuid = card.Uuid, finish }, tx, cancellationToken: ct)) is { } countedId)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE deck_cards SET quantity = quantity + @quantity WHERE id = @countedId",
                new { quantity, countedId }, tx, cancellationToken: ct));
            return countedId;
        }

        var categoryIds = new List<long>();
        foreach (var category in categories)
            categoryIds.Add(await ResolveCategoryAsync(conn, tx, deckId, category, ct));
        if (categoryIds.Count == 0)
            categoryIds.Add(await ResolveCategoryAsync(conn, tx, deckId, new DeckTextCategory(TypeCategoryOf(card)), ct));
        categoryIds = categoryIds.Distinct().ToList();

        if (await FindTwinAsync(conn, tx, deckId, card.Uuid, finish, categoryIds[0], null, ct) is { } existingId)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE deck_cards SET quantity = quantity + @quantity WHERE id = @existingId",
                new { quantity, existingId }, tx, cancellationToken: ct));
            return existingId;
        }

        var deckCardId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO deck_cards (deck_id, card_uuid, quantity, finish, added_at)
            VALUES (@deckId, @uuid, @quantity, @finish, @now);
            SELECT last_insert_rowid();
            """, new { deckId, uuid = card.Uuid, quantity, finish, now = Now() }, tx, cancellationToken: ct));
        await SetLinksAsync(conn, tx, deckCardId, categoryIds, ct);
        return deckCardId;
    }

    /// <summary>Another row of the deck with this printing, finish and primary category (excluding <paramref name="exceptId"/>).</summary>
    private static Task<long?> FindTwinAsync(DbConnection conn, DbTransaction tx, long deckId, string uuid, string finish,
        long primaryCategoryId, long? exceptId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT dc.id FROM deck_cards dc
            WHERE dc.deck_id = @deckId AND dc.card_uuid = @uuid AND dc.finish = @finish
              AND (@exceptId IS NULL OR dc.id <> @exceptId)
              AND (SELECT l.category_id FROM deck_card_categories l WHERE l.deck_card_id = dc.id
                   ORDER BY l.position, l.category_id LIMIT 1) = @primaryCategoryId
            ORDER BY dc.id
            LIMIT 1
            """, new { deckId, uuid, finish, primaryCategoryId, exceptId }, tx, cancellationToken: ct));

    /// <summary>
    /// If another row has the same printing, finish and primary category as <paramref name="deckCardId"/>, moves this row's
    /// quantity and extra categories into it and deletes this row. Returns the surviving row id.
    /// </summary>
    private static async Task<long> MergeIntoTwinAsync(DbConnection conn, DbTransaction tx, long deckCardId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<DeckCardRow>(new CommandDefinition(
            $"SELECT {DeckCardColumns} FROM deck_cards WHERE id = @deckCardId", new { deckCardId }, tx, cancellationToken: ct));
        if (row is null) return deckCardId; // already merged away
        var categoryIds = await CardCategoryIdsAsync(conn, tx, deckCardId, ct);
        if (categoryIds.Count == 0) return deckCardId;
        if (await FindTwinAsync(conn, tx, row.DeckId, row.CardUuid, row.Finish, categoryIds[0], deckCardId, ct) is not { } twinId)
            return deckCardId;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE deck_cards SET quantity = quantity + @Quantity WHERE id = @twinId",
            new { row.Quantity, twinId }, tx, cancellationToken: ct));
        var merged = (await CardCategoryIdsAsync(conn, tx, twinId, ct)).Concat(categoryIds).Distinct().ToList();
        await SetLinksAsync(conn, tx, twinId, merged, ct);
        await DeleteDeckCardAsync(conn, tx, deckCardId, ct);
        return twinId;
    }

    /// <summary>Type category of a card by precedence; "Other" when none applies or the card is unknown.</summary>
    internal static string TypeCategoryOf(DeckCardFacts? card)
    {
        if (card is null) return OtherTypeCategory;
        var types = card.TypeList;
        return TypePrecedence.FirstOrDefault(types.Contains) ?? OtherTypeCategory;
    }

    /// <summary>
    /// Category id for a name: same name (case-insensitive) → that one; a built-in name ("Sideboard", "SB", "Maybe",
    /// "Commander") → the category with that role (even when renamed); else a new regular category.
    /// </summary>
    private static async Task<long> ResolveCategoryAsync(DbConnection conn, DbTransaction tx, long deckId, DeckTextCategory category,
        CancellationToken ct) =>
        await FindCategoryAsync(conn, tx, deckId, category.Name, ct)
        ?? await InsertRegularCategoryAsync(conn, tx, deckId, category.Name, category.CountsInDeck, ct);

    /// <summary>The lookup part of <see cref="ResolveCategoryAsync"/> (null when a new category would be needed).</summary>
    private static async Task<long?> FindCategoryAsync(DbConnection conn, DbTransaction tx, long deckId, string name, CancellationToken ct)
    {
        var byName = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM deck_categories WHERE deck_id = @deckId AND name = @name COLLATE NOCASE",
            new { deckId, name }, tx, cancellationToken: ct));
        if (byName is not null || DeckCategoryRoles.FromName(name) is not { } role) return byName;

        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM deck_categories WHERE deck_id = @deckId AND role = @role",
            new { deckId, role }, tx, cancellationToken: ct));
    }

    /// <summary>Inserts a category after the last regular/Commander category (Sideboard and Maybeboard stay behind it).</summary>
    private static async Task<long> InsertRegularCategoryAsync(DbConnection conn, DbTransaction tx, long deckId, string name,
        bool countsInDeck, CancellationToken ct)
    {
        var position = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COALESCE(MAX(position) + 1, 0) FROM deck_categories
            WHERE deck_id = @deckId AND (role IS NULL OR role = 'commander')
            """, new { deckId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE deck_categories SET position = position + 1 WHERE deck_id = @deckId AND position >= @position",
            new { deckId, position }, tx, cancellationToken: ct));
        return await InsertCategoryRowAsync(conn, tx, deckId, name, position, countsInDeck, null, ct);
    }

    private static Task<long> InsertCategoryRowAsync(DbConnection conn, DbTransaction tx, long deckId, string name, int position,
        bool countsInDeck, string? role, CancellationToken ct) =>
        conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO deck_categories (deck_id, name, position, counts_in_deck, role)
            VALUES (@deckId, @name, @position, @countsInDeck, @role);
            SELECT last_insert_rowid();
            """, new { deckId, name, position, countsInDeck, role }, tx, cancellationToken: ct));

    /// <summary>Rewrites positions 0..n-1 in the current order, optionally moving one category to a new position.</summary>
    private static async Task RenumberCategoriesAsync(DbConnection conn, DbTransaction tx, long deckId, (long Id, int Position)? move,
        CancellationToken ct)
    {
        var ids = (await CategoriesAsync(conn, tx, deckId, ct)).Select(c => c.Id).ToList();
        if (move is { } m && ids.Remove(m.Id))
            ids.Insert(Math.Clamp(m.Position, 0, ids.Count), m.Id);
        for (var i = 0; i < ids.Count; i++)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE deck_categories SET position = @i WHERE id = @id", new { i, id = ids[i] }, tx, cancellationToken: ct));
    }

    private static async Task<List<CategoryRow>> CategoriesAsync(DbConnection conn, DbTransaction? tx, long deckId, CancellationToken ct) =>
        (await conn.QueryAsync<CategoryRow>(new CommandDefinition(
            $"SELECT {CategoryColumns} FROM deck_categories WHERE deck_id = @deckId ORDER BY position, id",
            new { deckId }, tx, cancellationToken: ct))).AsList();

    private static async Task<List<long>> CardCategoryIdsAsync(DbConnection conn, DbTransaction tx, long deckCardId, CancellationToken ct) =>
        (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT category_id FROM deck_card_categories WHERE deck_card_id = @deckCardId ORDER BY position, category_id",
            new { deckCardId }, tx, cancellationToken: ct))).AsList();

    /// <summary>Replaces a card's category links; the first id becomes the primary category.</summary>
    private static async Task SetLinksAsync(DbConnection conn, DbTransaction tx, long deckCardId, IReadOnlyList<long> categoryIds,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM deck_card_categories WHERE deck_card_id = @deckCardId", new { deckCardId }, tx, cancellationToken: ct));
        for (var i = 0; i < categoryIds.Count; i++)
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO deck_card_categories (deck_card_id, category_id, position) VALUES (@deckCardId, @categoryId, @i)",
                new { deckCardId, categoryId = categoryIds[i], i }, tx, cancellationToken: ct));
    }

    private static Task DeleteDeckCardAsync(DbConnection conn, DbTransaction tx, long deckCardId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM deck_card_categories WHERE deck_card_id = @deckCardId;
            DELETE FROM deck_cards WHERE id = @deckCardId;
            """, new { deckCardId }, tx, cancellationToken: ct));

    private static Task TouchAsync(DbConnection conn, DbTransaction tx, long deckId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "UPDATE decks SET updated_at = @now WHERE id = @deckId", new { deckId, now = Now() }, tx, cancellationToken: ct));

    private static async Task<DeckRow> RequireDeckAsync(DbConnection conn, DbTransaction? tx, long deckId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<DeckRow>(new CommandDefinition(
            $"SELECT {DeckColumns} FROM decks WHERE id = @deckId AND deleted_at IS NULL", new { deckId }, tx, cancellationToken: ct))
        ?? throw new DeckNotFoundException($"Deck {deckId} not found.");

    private static async Task<DeckCardRow> RequireDeckCardAsync(DbConnection conn, DbTransaction tx, long deckCardId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<DeckCardRow>(new CommandDefinition("""
            SELECT dc.id, dc.deck_id, dc.card_uuid, dc.quantity, dc.finish, dc.added_at
            FROM deck_cards dc JOIN decks d ON d.id = dc.deck_id
            WHERE dc.id = @deckCardId AND d.deleted_at IS NULL
            """, new { deckCardId }, tx, cancellationToken: ct))
        ?? throw new DeckNotFoundException($"Deck card {deckCardId} not found.");

    private static async Task<CategoryRow> RequireCategoryAsync(DbConnection conn, DbTransaction tx, long categoryId, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition("""
            SELECT c.id, c.deck_id, c.name, c.position, c.counts_in_deck, c.role
            FROM deck_categories c JOIN decks d ON d.id = c.deck_id
            WHERE c.id = @categoryId AND d.deleted_at IS NULL
            """, new { categoryId }, tx, cancellationToken: ct))
        ?? throw new DeckNotFoundException($"Category {categoryId} not found.");

    private static string ValidateDeckName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("A deck needs a name.", nameof(name));
        if (n.Length > MaxNameLength) throw new ArgumentException($"Deck names can have at most {MaxNameLength} characters.", nameof(name));
        return n;
    }

    private static string? CleanDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>Characters that would break the text format (<c>[A,B]</c>, <c>{flags}</c>, <c>^tags^</c>) aren't allowed.</summary>
    private static readonly char[] ForbiddenCategoryChars = ['[', ']', ',', '{', '}', '^', '\n', '\r'];

    private static string ValidateCategoryName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("A category needs a name.", nameof(name));
        if (n.Length > MaxCategoryNameLength)
            throw new ArgumentException($"Category names can have at most {MaxCategoryNameLength} characters.", nameof(name));
        if (n.IndexOfAny(ForbiddenCategoryChars) >= 0)
            throw new ArgumentException("Category names can't contain [ ] , { } or ^.", nameof(name));
        return n;
    }

    private static string SanitizeCategoryName(string name)
    {
        var n = new string((name ?? "").Where(ch => Array.IndexOf(ForbiddenCategoryChars, ch) < 0).ToArray()).Trim();
        return n.Length > MaxCategoryNameLength ? n[..MaxCategoryNameLength].Trim() : n;
    }

    private static string ValidateFinish(DeckCardFacts card, string finish)
    {
        var f = (finish ?? "").Trim().ToLowerInvariant();
        if (!card.FinishList.Contains(f))
            throw new ArgumentException($"{card.Name} ({card.SetCode.ToLowerInvariant()} {card.Number}) doesn't exist in “{finish}” " +
                                        $"(available: {string.Join(", ", card.FinishList)}).", nameof(finish));
        return f;
    }
}
