using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoxSynth.Cards;
using MoxSynth.Collection;

namespace MoxSynth.Decks;

/// <summary>A category named on an import line; <see cref="CountsInDeck"/> is false for Archidekt's <c>{noDeck}</c> flag.</summary>
public sealed record DeckTextCategory(string Name, bool CountsInDeck = true);

/// <summary>One card line of a text decklist.</summary>
public sealed record DeckTextLine(
    int LineNumber,
    string Text,
    int Quantity,
    string Name,
    string? SetCode,
    string? Number,
    string Finish,
    IReadOnlyList<DeckTextCategory> Categories);

public sealed record DeckTextParseResult(IReadOnlyList<DeckTextLine> Lines, IReadOnlyList<DeckImportLine> Errors);

/// <summary>A text line resolved to a printing (or not).</summary>
public sealed record DeckTextMatch(DeckTextLine Line, CardRef? Card, string Finish, string? Problem, string? Warning);

/// <summary>
/// Plain-text decklists as used by Archidekt, Moxfield and MTG Arena:
/// <c>&lt;qty&gt;[x] &lt;name&gt; [(&lt;set&gt;) [&lt;number&gt;]] [*F*|*E*] [[Category1,Category2]]</c>.
/// Section headers (<c>Commander</c>, <c>Sideboard</c>, <c>Maybeboard</c>, <c>Deck</c>, <c>// Ramp</c>, <c>Ramp:</c>) set the
/// category of the lines that follow; a line's own [categories] win. Blank lines and <c># comments</c> are skipped.
/// Archidekt extras are tolerated: <c>^tag,#color^</c> is ignored and <c>{flags}</c> are removed from category names
/// (<c>{noDeck}</c> makes a newly created category not count).
/// </summary>
public static partial class DeckText
{
    /// <summary>Bare header lines that don't need "//" or ":". Main-deck headers reset the category (type categories).</summary>
    private static readonly HashSet<string> MainHeaders = new(StringComparer.OrdinalIgnoreCase) { "deck", "main", "mainboard", "main deck" };

    private static readonly HashSet<string> BoardHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "commander", "commanders", "sideboard", "maybeboard", "maybe", "considering", "companion" };

    public static DeckTextParseResult Parse(string? text)
    {
        var lines = new List<DeckTextLine>();
        var errors = new List<DeckImportLine>();
        IReadOnlyList<DeckTextCategory> section = [];

        var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var lineNumber = i + 1;
            var line = raw[i].Trim();
            if (i == 0 && line.Length > 0 && line[0] == '﻿') line = line[1..].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // MTGO style "SB: 2 Lightning Bolt".
            IReadOnlyList<DeckTextCategory>? lineSection = null;
            if (line.StartsWith("SB:", StringComparison.OrdinalIgnoreCase))
            {
                line = line[3..].Trim();
                lineSection = [new DeckTextCategory("Sideboard")];
            }

            if (lineSection is null && TryParseHeader(line, out var header))
            {
                section = header is null ? [] : [ParseCategory(header)];
                continue;
            }

            var m = QuantityRegex().Match(line);
            int quantity;
            string rest;
            if (m.Success)
            {
                if (!int.TryParse(m.Groups["qty"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity is < 1 or > 1000)
                {
                    errors.Add(new DeckImportLine(lineNumber, raw[i].Trim(), $"Invalid quantity “{m.Groups["qty"].Value}”."));
                    continue;
                }
                rest = m.Groups["rest"].Value;
            }
            else
            {
                quantity = 1; // "Sol Ring" without a quantity
                rest = line;
            }

            var finish = CardFinishes.NonFoil;
            IReadOnlyList<DeckTextCategory>? categories = null;
            // Trailing decorations, in any order.
            for (var changed = true; changed;)
            {
                changed = false;
                if (TagsRegex().Match(rest) is { Success: true } tags)
                {
                    rest = rest[..tags.Index];
                    changed = true;
                }
                if (categories is null && CategoriesRegex().Match(rest) is { Success: true } cats)
                {
                    categories = cats.Groups["cats"].Value
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(ParseCategory)
                        .Where(c => c.Name.Length > 0)
                        .ToList();
                    rest = rest[..cats.Index];
                    changed = true;
                }
                if (FinishRegex().Match(rest) is { Success: true } f)
                {
                    finish = char.ToUpperInvariant(f.Groups["f"].Value[0]) == 'E' ? CardFinishes.Etched : CardFinishes.Foil;
                    rest = rest[..f.Index];
                    changed = true;
                }
            }

            rest = rest.Trim();
            string? setCode = null, number = null;
            if (SetRegex().Match(rest) is { Success: true } s)
            {
                rest = s.Groups["name"].Value.Trim();
                setCode = s.Groups["set"].Value.ToUpperInvariant();
                number = s.Groups["num"].Success ? s.Groups["num"].Value : null;
            }

            if (rest.Length == 0)
            {
                errors.Add(new DeckImportLine(lineNumber, raw[i].Trim(), "No card name."));
                continue;
            }

            lines.Add(new DeckTextLine(lineNumber, raw[i].Trim(), quantity, rest, setCode, number, finish,
                categories is { Count: > 0 } ? categories : lineSection ?? section));
        }

        return new DeckTextParseResult(lines, errors);
    }

    /// <summary>
    /// Finds the printing: set + number (when the name matches), else name + set (best printing in that set), else
    /// name (the most recent regular paper printing). Uses the same ranking as the Archidekt collection import.
    /// </summary>
    public static DeckTextMatch Resolve(DeckTextLine line, CardIndex index)
    {
        var printings = index.ByName(line.Name);
        if (printings.Count == 0 && line.Name.Contains(" / ") && !line.Name.Contains(" // "))
            printings = index.ByName(line.Name.Replace(" / ", " // "));

        if (index.BySetAndNumber(line.SetCode, line.Number) is { } exact &&
            (printings.Any(p => p.Uuid == exact.Uuid) || exact.Name.Equals(line.Name, StringComparison.OrdinalIgnoreCase)))
            return Accept(line, exact, null);

        if (printings.Count == 0)
            return new DeckTextMatch(line, null, line.Finish, $"Unknown card “{line.Name}”.", null);

        string? warning = null;
        if (line.SetCode is not null)
        {
            var inSet = printings.Where(p => p.SetCode.Equals(line.SetCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inSet.Count > 0)
            {
                if (line.Number is not null) warning = $"No #{line.Number} in {line.SetCode.ToLowerInvariant()}; used another printing from that set.";
                return Accept(line, ArchidektImporter.BestPrinting(inSet, line.Finish), warning);
            }
            warning = $"Not in set {line.SetCode.ToLowerInvariant()}; used the newest regular printing.";
        }
        return Accept(line, ArchidektImporter.BestPrinting(printings, line.Finish), warning);
    }

    private static DeckTextMatch Accept(DeckTextLine line, CardRef card, string? warning)
    {
        // Keep the finish only if this printing exists in it (e.g. *F* for a non-foil-only printing → non-foil).
        var finish = card.FinishList.Contains(line.Finish) ? line.Finish : card.FinishList[0];
        return new DeckTextMatch(line, card, finish, null, warning);
    }

    /// <summary>
    /// One line per card (<c>1x Sol Ring (cmm) 400 *F* [Ramp,Staples]</c>), grouped by primary category with a blank line
    /// between groups: the Commander category first, then the others by position. Within a group sorted by name.
    /// </summary>
    public static string Format(DeckDetail deck)
    {
        var categories = deck.Categories.ToDictionary(c => c.Id);
        var groupOrder = deck.Categories
            .OrderBy(c => c.Role == DeckCategoryRoles.Commander ? 0 : 1)
            .ThenBy(c => c.Position)
            .Select(c => c.Id)
            .ToList();

        var groups = deck.Cards
            .GroupBy(c => c.PrimaryCategoryId ?? -1)
            .OrderBy(g => groupOrder.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i);

        var sb = new StringBuilder();
        foreach (var group in groups)
        {
            if (sb.Length > 0) sb.Append('\n');
            foreach (var card in group
                         .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.SetCode, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Number, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Finish))
            {
                sb.Append(card.Quantity).Append("x ").Append(card.Name);
                if (card.SetCode.Length > 0) sb.Append(" (").Append(card.SetCode.ToLowerInvariant()).Append(')');
                if (card.Number.Length > 0) sb.Append(' ').Append(card.Number);
                if (card.Finish == CardFinishes.Foil) sb.Append(" *F*");
                else if (card.Finish == CardFinishes.Etched) sb.Append(" *E*");
                var names = card.CategoryIds.Where(categories.ContainsKey).Select(id => categories[id].Name).ToList();
                if (names.Count > 0) sb.Append(" [").Append(string.Join(',', names)).Append(']');
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Section header → category name; null name = back to the main deck (type categories).</summary>
    private static bool TryParseHeader(string line, out string? category)
    {
        category = null;
        string name;
        if (line.StartsWith("//"))
            name = line[2..];
        else if (line.EndsWith(':') && !char.IsAsciiDigit(line[0]))
            name = line[..^1];
        else if (!char.IsAsciiDigit(line[0]) && (BoardHeaders.Contains(StripCount(line)) || MainHeaders.Contains(StripCount(line))))
            name = line;
        else
            return false;

        name = StripCount(name.Trim().TrimEnd(':').Trim());
        if (name.Length == 0) return true; // a bare "//" – keep the current section
        category = MainHeaders.Contains(name) ? null : name;
        return true;
    }

    /// <summary>"Sideboard (15)" → "Sideboard".</summary>
    private static string StripCount(string header) => CountSuffixRegex().Replace(header.Trim(), "").Trim();

    private static DeckTextCategory ParseCategory(string raw)
    {
        var noDeck = raw.Contains("{noDeck}", StringComparison.OrdinalIgnoreCase);
        var name = FlagsRegex().Replace(raw, "").Trim();
        return new DeckTextCategory(name, !noDeck);
    }

    [GeneratedRegex(@"^(?<qty>\d+)\s*[xX]?\s+(?<rest>.+)$")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"\s*\^[^\^]*\^\s*$")]
    private static partial Regex TagsRegex();

    [GeneratedRegex(@"\s*\[(?<cats>[^\[\]]*)\]\s*$")]
    private static partial Regex CategoriesRegex();

    [GeneratedRegex(@"\s*\*(?<f>[FfEe])\*\s*$")]
    private static partial Regex FinishRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+\((?<set>[A-Za-z0-9]{2,8})\)(?:\s+(?<num>\S+))?$")]
    private static partial Regex SetRegex();

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CountSuffixRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex FlagsRegex();
}
