using MoxSynth.Decks;

namespace MoxSynth.Components.Shared;

/// <summary>One group of cards in the deck editor (a table section or a stacks column).</summary>
public sealed record DeckCardGroup(string Key, string Name, IReadOnlyList<DeckCardView> Cards, DeckCategoryView? Category, bool Muted)
{
    public int Quantity => Cards.Sum(c => c.Quantity);
}

/// <summary>Grouping shared by the deck editor's table and stacks views, so both always agree.</summary>
public static class DeckGrouping
{
    public const string ByCategory = "category";
    public const string ByType = "type";
    public const string ByManaValue = "manavalue";

    /// <summary>Highest mana value with a group of its own; higher values share a "7+" group.</summary>
    public const int MaxManaValueGroup = 7;

    /// <summary>Type groups, in display order (a card goes to the first of <see cref="TypePrecedence"/> it has).</summary>
    public static readonly string[] TypeDisplayOrder =
        ["Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Artifact", "Enchantment", "Land", "Other"];

    /// <summary>Same precedence as the deck's type categories (Land wins over Artifact and Enchantment).</summary>
    public static readonly string[] TypePrecedence =
        ["Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Land", "Artifact", "Enchantment"];

    public const string OtherType = "Other";
    public const string LandType = "Land";

    /// <summary>Commander first, Sideboard and Maybeboard last, the rest by position in between.</summary>
    public static int CategoryRank(DeckCategoryView category) => category.Role switch
    {
        DeckCategoryRoles.Commander => 0,
        DeckCategoryRoles.Sideboard or DeckCategoryRoles.Maybeboard => 2,
        _ => 1,
    };

    public static IEnumerable<DeckCategoryView> OrderedCategories(DeckDetail? deck) =>
        deck is null ? [] : deck.Categories.OrderBy(CategoryRank).ThenBy(c => c.Position);

    public static IReadOnlyList<DeckCardView> Sorted(IEnumerable<DeckCardView> cards) =>
        cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.SetCode).ThenBy(c => c.Number).ToList();

    /// <summary>The card's primary type: the first of <see cref="TypePrecedence"/> it has, else "Other".</summary>
    public static string PrimaryType(DeckCardView card) =>
        TypePrecedence.FirstOrDefault(t => card.Types.Contains(t)) ?? OtherType;

    /// <summary>Cards whose primary category counts toward the deck (cards without a known category count).</summary>
    public static IEnumerable<DeckCardView> CountedCards(DeckDetail deck)
    {
        var categories = deck.Categories.ToDictionary(c => c.Id);
        return deck.Cards.Where(c => c.PrimaryCategoryId is not { } id || !categories.TryGetValue(id, out var cat) || cat.CountsInDeck);
    }

    /// <summary>Mana value group of a non-land card: 0…6, or 7 for "7+".</summary>
    public static int ManaValueBucket(DeckCardView card) =>
        Math.Clamp((int)Math.Floor(card.ManaValue), 0, MaxManaValueGroup);

    public static string ManaValueLabel(int bucket) =>
        bucket >= MaxManaValueGroup ? $"{MaxManaValueGroup}+" : bucket.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static List<DeckCardGroup> Build(DeckDetail? deck, string groupBy)
    {
        if (deck is null) return [];
        var categories = deck.Categories.ToDictionary(c => c.Id);
        DeckCategoryView? PrimaryOf(DeckCardView card) =>
            card.PrimaryCategoryId is { } id && categories.TryGetValue(id, out var category) ? category : null;

        if (groupBy != ByType && groupBy != ByManaValue)
        {
            var groups = OrderedCategories(deck)
                .Select(cat => new DeckCardGroup($"c{cat.Id}", cat.Name, Sorted(deck.Cards.Where(c => c.PrimaryCategoryId == cat.Id)), cat, !cat.CountsInDeck))
                .Where(g => g.Cards.Count > 0)
                .ToList();
            var orphans = deck.Cards.Where(c => PrimaryOf(c) is null).ToList();
            if (orphans.Count > 0) groups.Add(new DeckCardGroup("c-none", "Uncategorized", Sorted(orphans), null, false));
            return groups;
        }

        var counted = CountedCards(deck).ToList();
        List<DeckCardGroup> result;
        if (groupBy == ByType)
        {
            result = counted.GroupBy(PrimaryType)
                .OrderBy(g => Array.IndexOf(TypeDisplayOrder, g.Key))
                .Select(g => new DeckCardGroup($"t{g.Key}", g.Key, Sorted(g), null, false))
                .ToList();
        }
        else
        {
            result = counted.Where(c => PrimaryType(c) != LandType)
                .GroupBy(ManaValueBucket)
                .OrderBy(g => g.Key)
                .Select(g => new DeckCardGroup($"m{g.Key}", $"Mana value {ManaValueLabel(g.Key)}", Sorted(g), null, false))
                .ToList();
            var lands = counted.Where(c => PrimaryType(c) == LandType).ToList();
            if (lands.Count > 0) result.Add(new DeckCardGroup("m-land", LandType, Sorted(lands), null, false));
        }

        // Sideboard and Maybeboard (when they don't count) as their own groups at the end.
        foreach (var board in deck.Categories
                     .Where(c => c.Role is DeckCategoryRoles.Sideboard or DeckCategoryRoles.Maybeboard && !c.CountsInDeck)
                     .OrderBy(c => c.Role == DeckCategoryRoles.Sideboard ? 0 : 1))
        {
            var cards = Sorted(deck.Cards.Where(c => c.PrimaryCategoryId == board.Id));
            if (cards.Count > 0) result.Add(new DeckCardGroup($"b{board.Id}", board.Name, cards, null, true));
        }
        return result;
    }
}
