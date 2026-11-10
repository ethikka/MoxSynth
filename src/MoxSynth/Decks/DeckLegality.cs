using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoxSynth.Decks;

/// <summary>Where a card sits for the legality rules (decided by its primary category).</summary>
public enum DeckBoard
{
    /// <summary>A counting category (the main deck).</summary>
    Main,
    /// <summary>The Commander category of a commander-style format.</summary>
    Commander,
    Sideboard,
    Maybeboard,
    /// <summary>Another category that doesn't count in the deck – ignored like the Maybeboard.</summary>
    Excluded,
}

/// <summary>One deck entry as seen by <see cref="DeckLegality"/>.</summary>
/// <param name="Legality">cardLegalities value for the format: "Legal", "Banned", "Restricted"; null = not legal.</param>
/// <param name="DeckLimit">From the card text: null = normal rules, int.MaxValue = "any number", N = "up to N".</param>
/// <param name="IsKnown">False when the printing is not in the card catalogue.</param>
public sealed record DeckLegalityCard(
    long DeckCardId,
    string Name,
    int Quantity,
    DeckBoard Board,
    string? Legality,
    bool IsBasicLand,
    int? DeckLimit,
    IReadOnlyList<string> ColorIdentity,
    bool CanBeCommander,
    bool IsKnown = true);

/// <summary>
/// Deck legality, simplified from the official rules:
/// <list type="bullet">
/// <item><c>casual</c>: always legal.</item>
/// <item>Every considered card must be Legal (or Restricted → max 1 copy) in <c>cardLegalities.&lt;format&gt;</c>.</item>
/// <item>Commander-style: exactly 100 cards (brawl/standardbrawl/oathbreaker: 60) including the commander(s); singleton
/// (except basic lands and "any number" cards); 1–2 commanders that can lead the deck (not checked for oathbreaker,
/// partner rules aren't checked); every card within the commanders' combined colour identity. Sideboard is ignored.</item>
/// <item>Constructed: at least 60 cards, sideboard max 15, max 4 copies of a name across main deck and sideboard.</item>
/// <item>Maybeboard and other non-counting categories are ignored.</item>
/// </list>
/// </summary>
public static partial class DeckLegality
{
    private const int ConstructedMinimum = 60;
    private const int SideboardMaximum = 15;
    private const int ConstructedCopies = 4;

    public static DeckLegalityResult Evaluate(string format, IReadOnlyList<DeckLegalityCard> cards)
    {
        if (format == DeckFormats.Casual) return new DeckLegalityResult();

        var problems = new List<string>();
        var reasons = new Dictionary<long, string>();
        var label = DeckFormats.Label(format);
        var commanderStyle = DeckFormats.IsCommanderStyle(format);

        void CardProblem(DeckLegalityCard card, string reason)
        {
            reasons.TryAdd(card.DeckCardId, char.ToUpperInvariant(reason[0]) + reason[1..]);
            var problem = $"{card.Name}: {reason}";
            if (!problems.Contains(problem)) problems.Add(problem);
        }

        // Cards that take part in the rules.
        var main = cards.Where(c => c.Board is DeckBoard.Main or DeckBoard.Commander).ToList();
        var sideboard = commanderStyle ? [] : cards.Where(c => c.Board == DeckBoard.Sideboard).ToList();
        var considered = main.Concat(sideboard).ToList();

        // Deck-level rules first.
        var mainCount = main.Sum(c => c.Quantity);
        IReadOnlyList<string> identity = [];
        if (commanderStyle)
        {
            var size = DeckFormats.CommanderDeckSize(format)!.Value;
            if (mainCount != size) problems.Add($"{mainCount} of {size} cards");

            var commanders = cards.Where(c => c.Board == DeckBoard.Commander).ToList();
            var commanderCount = commanders.Sum(c => c.Quantity);
            if (commanderCount == 0) problems.Add("No commander");
            else if (commanderCount > 2) problems.Add($"{commanderCount} commanders (max 2)");

            if (format != "oathbreaker")
                foreach (var c in commanders.Where(c => c.IsKnown && !c.CanBeCommander))
                    CardProblem(c, "can't be a commander");

            identity = commanders.SelectMany(c => c.ColorIdentity).Distinct().ToList();
        }
        else
        {
            if (mainCount < ConstructedMinimum) problems.Add($"{mainCount} cards (min {ConstructedMinimum})");
            var sideboardCount = sideboard.Sum(c => c.Quantity);
            if (sideboardCount > SideboardMaximum) problems.Add($"Sideboard: {sideboardCount} cards (max {SideboardMaximum})");
        }

        // Card legality in the format.
        foreach (var c in considered)
        {
            if (!c.IsKnown) CardProblem(c, "unknown card");
            else if (string.Equals(c.Legality, "Banned", StringComparison.OrdinalIgnoreCase)) CardProblem(c, $"banned in {label}");
            else if (c.Legality is null || !(c.Legality.Equals("Legal", StringComparison.OrdinalIgnoreCase)
                                            || c.Legality.Equals("Restricted", StringComparison.OrdinalIgnoreCase)))
                CardProblem(c, $"not legal in {label}");
        }

        // Copy limits per card name.
        foreach (var group in considered.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var copies = group.Sum(c => c.Quantity);
            var any = group.First();
            var restricted = group.Any(c => string.Equals(c.Legality, "Restricted", StringComparison.OrdinalIgnoreCase));
            int max;
            if (any.IsBasicLand) max = int.MaxValue;
            else if (any.DeckLimit is { } limit) max = limit;
            else max = commanderStyle ? 1 : ConstructedCopies;
            if (restricted) max = Math.Min(max, 1);

            if (copies > max)
                foreach (var c in group)
                    CardProblem(c, restricted ? $"{copies} copies (restricted, max 1)" : $"{copies} copies (max {max})");
        }

        // Colour identity.
        if (commanderStyle && cards.Any(c => c.Board == DeckBoard.Commander))
        {
            foreach (var c in main.Where(c => c.Board == DeckBoard.Main && c.ColorIdentity.Any(color => !identity.Contains(color))))
                CardProblem(c, "outside the commander's color identity");
        }

        return new DeckLegalityResult { Problems = problems, CardReasons = reasons };
    }

    /// <summary>"A deck can have any number of cards named …" → int.MaxValue; "… up to seven cards named …" → 7.</summary>
    internal static int? DeckLimitFromText(string? text, bool hasAlternativeDeckLimit)
    {
        if (!hasAlternativeDeckLimit && (text is null || !text.Contains("A deck can have", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (text is null) return int.MaxValue;
        var m = UpToRegex().Match(text);
        if (!m.Success) return int.MaxValue;
        var word = m.Groups[1].Value.ToLowerInvariant();
        if (int.TryParse(word, out var n)) return n;
        string[] words = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"];
        var index = Array.IndexOf(words, word);
        return index >= 0 ? index : int.MaxValue;
    }

    /// <summary>Whether MTGJSON leadershipSkills allows this card to lead the format, falling back to "legendary creature".</summary>
    internal static bool CanBeCommander(string format, string? leadershipSkills, bool isLegendaryCreature)
    {
        var key = DeckFormats.LeadershipKey(format);
        if (key is not null && !string.IsNullOrWhiteSpace(leadershipSkills))
        {
            try
            {
                using var doc = JsonDocument.Parse(leadershipSkills);
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True) return true;
            }
            catch (JsonException)
            {
                // fall through to the type check
            }
        }
        return isLegendaryCreature && format is not ("oathbreaker" or "paupercommander");
    }

    [GeneratedRegex(@"A deck can have up to (\w+) cards named", RegexOptions.IgnoreCase)]
    private static partial Regex UpToRegex();
}
