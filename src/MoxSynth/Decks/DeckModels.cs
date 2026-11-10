using MoxSynth.Infrastructure;

namespace MoxSynth.Decks;

/// <summary>Built-in category roles (stored in <c>deck_categories.role</c>, so they survive renaming).</summary>
public static class DeckCategoryRoles
{
    public const string Commander = "commander";
    public const string Sideboard = "sideboard";
    public const string Maybeboard = "maybeboard";

    /// <summary>Role meant by a category name such as "Sideboard" or "SB" (used when importing), else null.</summary>
    public static string? FromName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "commander" or "commanders" => Commander,
        "sideboard" or "side" or "sb" => Sideboard,
        "maybeboard" or "maybe" or "considering" => Maybeboard,
        _ => null,
    };
}

/// <summary>Deck formats: MTGJSON legality keys (columns of <c>cardLegalities</c>) plus <see cref="Casual"/>.</summary>
public static class DeckFormats
{
    /// <summary>No rules: every deck is legal.</summary>
    public const string Casual = "casual";

    private static readonly HashSet<string> CommanderStyle =
        ["commander", "oathbreaker", "brawl", "standardbrawl", "paupercommander", "predh"];

    /// <summary>Formats with a Commander category, singleton and colour-identity rules.</summary>
    public static bool IsCommanderStyle(string format) => CommanderStyle.Contains(format);

    /// <summary>Exact deck size of a commander-style format (commander included), null for other formats.</summary>
    public static int? CommanderDeckSize(string format) => format switch
    {
        "brawl" or "standardbrawl" or "oathbreaker" => 60,
        _ when IsCommanderStyle(format) => 100,
        _ => null,
    };

    /// <summary>Key in MTGJSON <c>leadershipSkills</c> that says a card may lead a deck of this format.</summary>
    internal static string? LeadershipKey(string format) => format switch
    {
        "commander" => "commander",
        "predh" => "predh",
        "paupercommander" => "pauper_commander",
        "brawl" or "standardbrawl" => "brawl",
        "oathbreaker" => "oathbreaker",
        _ => null,
    };

    public static string Label(string format) => format switch
    {
        "casual" => "Casual",
        "standardbrawl" => "Standard Brawl",
        "competitivebrawl" => "Competitive Brawl",
        "paupercommander" => "Pauper Commander",
        "predh" => "PreDH",
        "oldschool" => "Old School",
        "tlr" => "Tales of Middle-earth",
        _ => format.Length == 0 ? format : char.ToUpperInvariant(format[0]) + format[1..],
    };
}

/// <summary>Deck list entry (ListAsync).</summary>
public sealed class DeckSummary
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Format { get; init; } = "";
    /// <summary>Sum of quantities of cards whose primary category counts in the deck.</summary>
    public int CardCount { get; init; }
    /// <summary>Cards in the Commander category (commander-style formats), in the order they were added.</summary>
    public IReadOnlyList<string> CommanderNames { get; init; } = [];
    /// <summary>First commander's image, else the most expensive counted card's, else the first counted card's (Scryfall "normal").</summary>
    public string? CoverImageUrl { get; init; }
    public DateTime UpdatedAt { get; init; }
    /// <summary>Value of the counted cards at the requested provider (null without prices).</summary>
    public double? Value { get; init; }
    public string? Currency { get; init; }
    public bool IsLegal { get; init; }
}

/// <summary>A deck with all its categories and cards (GetAsync).</summary>
public sealed class DeckDetail
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Format { get; init; } = "";
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Ordered by <see cref="DeckCategoryView.Position"/>.</summary>
    public IReadOnlyList<DeckCategoryView> Categories { get; init; } = [];
    /// <summary>Ordered by primary category position, then name.</summary>
    public IReadOnlyList<DeckCardView> Cards { get; init; } = [];
    public required DeckLegalityResult Legality { get; init; }

    /// <summary>Value of the counted cards at the requested provider (null without prices).</summary>
    public double? Value { get; init; }
    public string? Currency { get; init; }
    /// <summary>Counted cards covered by the collection: per card name min(deck quantity, owned copies of any printing).</summary>
    public int OwnedCount { get; init; }
    /// <summary>Sum of quantities of cards whose primary category counts in the deck.</summary>
    public int CardCount { get; init; }

    public IReadOnlyList<string> CommanderNames { get; init; } = [];
    public string? CoverImageUrl { get; init; }
}

public sealed class DeckCategoryView
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int Position { get; init; }
    public bool CountsInDeck { get; init; }
    /// <summary><see cref="DeckCategoryRoles"/> value for Commander / Sideboard / Maybeboard, else null.</summary>
    public string? Role { get; init; }
    /// <summary>Sum of quantities of the cards listed here (cards whose primary category this is).</summary>
    public int CardCount { get; init; }
}

public sealed record DeckCardView
{
    public long DeckCardId { get; init; }
    public string CardUuid { get; init; } = "";
    public string Name { get; init; } = "";
    public string SetCode { get; init; } = "";
    public string Number { get; init; } = "";
    public string Finish { get; init; } = "";
    public int Quantity { get; init; }
    /// <summary>Category ids, primary (where the card is listed) first.</summary>
    public IReadOnlyList<long> CategoryIds { get; init; } = [];
    public string? ManaCost { get; init; }
    public double ManaValue { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = [];
    public IReadOnlyList<string> ColorIdentity { get; init; } = [];
    public string? TypeLine { get; init; }
    public IReadOnlyList<string> Types { get; init; } = [];
    public string? Rarity { get; init; }
    public string? ImageUrl { get; init; }
    public string? SmallImageUrl { get; init; }
    /// <summary>Unit price for <see cref="Finish"/> at the requested provider.</summary>
    public double? Price { get; init; }
    /// <summary>Owned copies of this printing in this finish.</summary>
    public int OwnedExact { get; init; }
    /// <summary>Owned copies of any printing with the same name.</summary>
    public int OwnedAny { get; init; }
    /// <summary>Why this card makes the deck illegal (e.g. "Banned in Modern"), null when fine.</summary>
    public string? IllegalReason { get; init; }

    public long? PrimaryCategoryId => CategoryIds.Count > 0 ? CategoryIds[0] : null;
}

public sealed class DeckLegalityResult
{
    public bool IsLegal => Problems.Count == 0;
    /// <summary>Short human-readable problems ("97 of 100 cards", "Sol Ring: banned in Modern").</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>Per deck card id: why that card is illegal (only cards with a problem are present).</summary>
    public IReadOnlyDictionary<long, string> CardReasons { get; init; } = new Dictionary<long, string>();
}

/// <summary>Result of <see cref="DeckRepository.ImportTextAsync"/>.</summary>
public sealed class DeckImportResult
{
    /// <summary>Number of cards added (sum of quantities).</summary>
    public int Added { get; init; }
    /// <summary>Lines that could not be read or matched to a card (nothing was added for them).</summary>
    public IReadOnlyList<DeckImportLine> Unmatched { get; init; } = [];
    /// <summary>Lines that were added, but not quite as written (e.g. merged into an existing entry, set not found).</summary>
    public IReadOnlyList<DeckImportLine> Warnings { get; init; } = [];
}

public sealed record DeckImportLine(int Line, string Text, string Problem);

/// <summary>
/// An already-resolved card for <see cref="DeckRepository.ImportCardsAsync"/> (e.g. from an Archidekt deck).
/// <see cref="Line"/>/<see cref="Text"/> identify it in warnings and unmatched lists; the first category is primary.
/// </summary>
public sealed record DeckImportCard(int Line, string Text, string CardUuid, int Quantity, string Finish, IReadOnlyList<string> Categories);

/// <summary>A deck, card or category does not exist in this collection database, or its deck was deleted.</summary>
public sealed class DeckNotFoundException(string message) : KeyNotFoundException(message);

// ------------------------------------------------------------------ internal rows

internal sealed class DeckRow
{
    private DateTime _createdAt, _updatedAt;

    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get => _createdAt; set => _createdAt = SqliteTime.AsUtc(value); }
    public DateTime UpdatedAt { get => _updatedAt; set => _updatedAt = SqliteTime.AsUtc(value); }
}

internal sealed class CategoryRow
{
    public long Id { get; set; }
    public long DeckId { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public bool CountsInDeck { get; set; }
    public string? Role { get; set; }
}

internal sealed class DeckCardRow
{
    public long Id { get; set; }
    public long DeckId { get; set; }
    public string CardUuid { get; set; } = "";
    public int Quantity { get; set; }
    public string Finish { get; set; } = "";
    public string AddedAt { get; set; } = "";
}

internal sealed class CardCategoryRow
{
    public long DeckCardId { get; set; }
    public long CategoryId { get; set; }
    public int Position { get; set; }
}

/// <summary>Catalogue facts of one printing (primary face) needed for decks.</summary>
internal sealed class DeckCardFacts
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FaceName { get; set; }
    public string? Side { get; set; }
    public string SetCode { get; set; } = "";
    public string Number { get; set; } = "";
    public string? ManaCost { get; set; }
    public double? ManaValue { get; set; }
    public string? Colors { get; set; }
    public string? ColorIdentity { get; set; }
    public string? Type { get; set; }
    public string? Types { get; set; }
    public string? Supertypes { get; set; }
    public string? Rarity { get; set; }
    public string? Text { get; set; }
    public string? LeadershipSkills { get; set; }
    public long? HasAlternativeDeckLimit { get; set; }
    public string? Finishes { get; set; }
    public string? OtherFaceIds { get; set; }
    public string? ScryfallId { get; set; }

    /// <summary>cardLegalities per format ("Legal", "Banned", "Restricted"; absent/null = not legal). Filled separately.</summary>
    public Dictionary<string, string?> Legalities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public IReadOnlyList<string> TypeList => SplitList(Types);
    public IReadOnlyList<string> ColorList => SplitList(Colors);
    public IReadOnlyList<string> ColorIdentityList => SplitList(ColorIdentity);
    public IReadOnlyList<string> FinishList => Cards.CardFinishes.Parse(Finishes);

    public bool IsBasicLand => SplitList(Supertypes).Contains("Basic") && TypeList.Contains("Land");
    public bool IsLegendaryCreature => SplitList(Supertypes).Contains("Legendary") && TypeList.Contains("Creature");
}
