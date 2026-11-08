using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Infrastructure;

namespace MoxSynth.Cards;

/// <summary>What the scanner recognised. <see cref="SetCode"/>/<see cref="Number"/> are set when the exact printing is known.</summary>
public sealed record CardRecognition(string Name, string? SetCode, string? Number, double Confidence)
{
    public bool IsExactPrinting => SetCode is not null && Number is not null;
}

/// <summary>An OCR'd line of a photo with its bounding box (pixels).</summary>
public sealed record OcrTextLine(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Turns OCR text from a card photo into a card: fuzzy-matches the title line against all card (face) names
/// and parses collector number + set code from the bottom-left info line ("0123/280 R" / "MH2 • EN").
/// </summary>
public sealed partial class CardRecognizer(DatabasePaths paths)
{
    private sealed record TitleEntry(string Normalized, string Name);

    private sealed class PrintingRow
    {
        public string Name { get; set; } = "";
        public string SetCode { get; set; } = "";
        public string Number { get; set; } = "";
    }

    private readonly object _lock = new();
    private TitleEntry[]? _titles;
    private HashSet<string>? _setCodes;

    private SqliteConnection Open() => new(paths.AllPrintingsConnectionString);

    public CardRecognition? Recognize(string? titleText, string? bottomText)
    {
        if (!File.Exists(paths.AllPrintingsPath)) return null;
        EnsureLoaded();

        var (number, setCode) = ParseBottomLine(bottomText ?? "");
        var (name, nameScore) = MatchTitle(titleText ?? "");

        using var conn = Open();

        // 1. Set code + collector number read → exact printing (confirmed by the title if we have one).
        if (setCode is not null && number is not null)
        {
            var printing = conn.QueryFirstOrDefault<PrintingRow>("""
                SELECT name, setCode, number FROM cards
                WHERE setCode = @setCode AND (number = @number OR ltrim(number, '0') = @number)
                  AND (side IS NULL OR side = 'a')
                LIMIT 1
                """, new { setCode, number });

            if (printing is not null)
            {
                var agrees = name is null || Similarity(Normalize(name), Normalize(printing.Name)) >= 0.6
                                          || Normalize(printing.Name).Contains(Normalize(name));
                if (agrees)
                    return new CardRecognition(printing.Name, printing.SetCode, printing.Number, name is null ? 0.7 : 0.98);
            }
        }

        if (name is null) return null;

        // 2. Title matched: narrow down to a printing with the number / set that was read, if unambiguous.
        var printings = conn.Query<PrintingRow>("""
            SELECT name, setCode, number FROM cards
            WHERE name = @name AND (side IS NULL OR side = 'a')
            """, new { name }).AsList();

        var candidates = printings
            .Where(p => number is null || p.Number.TrimStart('0') == number)
            .Where(p => setCode is null || p.SetCode == setCode)
            .ToList();

        if ((number is not null || setCode is not null) && candidates.Count == 1)
            return new CardRecognition(name, candidates[0].SetCode, candidates[0].Number, nameScore);

        if (setCode is not null && printings.Count(p => p.SetCode == setCode) >= 1)
            return new CardRecognition(name, setCode, null, nameScore);

        return new CardRecognition(name, null, null, nameScore);
    }

    // ------------------------------------------------------------------ whole photo (mobile app)

    /// <summary>
    /// Recognises a card from the OCR lines of a whole photo (the Android app uses on-device OCR of the full image).
    /// The title is searched among the top lines of the text area – also with trailing words dropped, since the
    /// mana cost is often read as junk at the end of the title line – and the info line among the bottom-left lines
    /// (left part only, so the copyright year on the right isn't taken for a collector number).
    /// Falls back to any line that matches a card name very well.
    /// </summary>
    public (CardRecognition? Result, string TitleText, string BottomText) RecognizeLines(IReadOnlyList<OcrTextLine> lines)
    {
        var usable = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Height > 0).ToList();
        if (usable.Count == 0 || !File.Exists(paths.AllPrintingsPath)) return (null, "", "");
        EnsureLoaded();

        var top = usable.Min(l => l.Y);
        var bottom = usable.Max(l => l.Y + l.Height);
        var left = usable.Min(l => l.X);
        var right = usable.Max(l => l.X + l.Width);
        var height = Math.Max(1, bottom - top);
        var width = Math.Max(1, right - left);

        // Title: best name match among the topmost lines.
        var titleLines = usable.Where(l => l.Y - top <= height * 0.15).OrderBy(l => l.Y).Take(4).ToList();

        string? titleText = null;
        var bestScore = 0.0;
        foreach (var line in titleLines)
        {
            foreach (var candidate in WordPrefixes(line.Text))
            {
                var (name, score) = MatchTitle(candidate);
                if (name is not null && score > bestScore)
                {
                    bestScore = score;
                    titleText = candidate;
                }
            }
        }

        // Fallback: card photographed with other text around it – any line that is almost exactly a card name.
        if (titleText is null)
        {
            foreach (var line in usable.OrderBy(l => l.Y).Take(40))
            {
                foreach (var candidate in WordPrefixes(line.Text))
                {
                    var (name, score) = MatchTitle(candidate);
                    if (name is not null && score >= 0.9 && score > bestScore)
                    {
                        bestScore = score;
                        titleText = candidate;
                    }
                }
            }
        }

        // Info line(s): bottom 18 % of the text area, left 65 %.
        var bottomText = string.Join('\n', usable
            .Where(l => l.Y + l.Height >= bottom - height * 0.18 && l.X - left <= width * 0.65)
            .OrderBy(l => l.Y).ThenBy(l => l.X)
            .Select(l => l.Text));

        var title = titleText ?? titleLines.FirstOrDefault()?.Text ?? "";
        return (Recognize(title, bottomText), title, bottomText);
    }

    /// <summary>"Lightning Bolt 3R x" → "Lightning Bolt 3R x", "Lightning Bolt 3R", "Lightning Bolt", "Lightning".</summary>
    private static IEnumerable<string> WordPrefixes(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = words.Length; n >= 1 && n >= words.Length - 4; n--)
            yield return string.Join(' ', words, 0, n);
    }

    // ------------------------------------------------------------------ bottom line

    [GeneratedRegex(@"(?<!\d)0*(\d{1,4})\s*[/|]\s*\d{1,4}(?!\d)")]
    private static partial Regex NumberSlashTotal();

    [GeneratedRegex(@"^0*(\d{1,4})[A-Z]?$")]
    private static partial Regex LooseNumber();

    [GeneratedRegex(@"[A-Z0-9]{3,5}")]
    private static partial Regex SetCodeToken();

    internal (string? Number, string? SetCode) ParseBottomLine(string text)
    {
        var upper = text.ToUpperInvariant();

        string? number = null;
        var slash = NumberSlashTotal().Match(upper);
        if (slash.Success)
        {
            number = slash.Groups[1].Value;
        }
        else
        {
            // Newer frames print just "0123" (no total). Take the first token that is purely a (zero padded) number.
            foreach (var token in upper.Split([' ', '\n', '\r', '\t', '•', '*', '·'], StringSplitOptions.RemoveEmptyEntries))
            {
                var m = LooseNumber().Match(token);
                if (m.Success)
                {
                    number = m.Groups[1].Value;
                    break;
                }
            }
        }

        string? setCode = null;
        foreach (Match m in SetCodeToken().Matches(upper))
        {
            var token = m.Value;
            if (token.All(char.IsAsciiDigit)) continue;
            if (_setCodes!.Contains(token)) { setCode = token; break; }
            // Common OCR confusion: 0 ↔ O, 1 ↔ I in set codes.
            var swapped = token.Replace('0', 'O').Replace('1', 'I');
            if (_setCodes.Contains(swapped)) { setCode = swapped; break; }
        }

        return (number, setCode);
    }

    // ------------------------------------------------------------------ title

    internal (string? Name, double Score) MatchTitle(string text)
    {
        var query = Normalize(text);
        if (query.Length < 3) return (null, 0);

        string? best = null;
        var bestScore = 0.0;
        foreach (var entry in _titles!)
        {
            var candidate = entry.Normalized;
            // Cheap length filter; OCR may cut off the end of long names, so allow longer candidates.
            if (candidate.Length < query.Length * 0.6 || candidate.Length > query.Length * 2.2) continue;

            var score = Similarity(query, candidate);
            if (candidate.Length > query.Length && query.Length >= 8)
                score = Math.Max(score, Similarity(query, candidate[..query.Length]) * 0.95); // truncated title

            if (score > bestScore)
            {
                bestScore = score;
                best = entry.Name;
                if (score >= 0.999) break;
            }
        }

        return bestScore >= 0.78 ? (best, bestScore) : (null, bestScore);
    }

    /// <summary>Lowercase, accents removed, only letters/digits/spaces – makes OCR noise and punctuation irrelevant.</summary>
    internal static string Normalize(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace && ch is ' ' or '-' or ',')
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> curr = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            curr.CopyTo(prev);
        }
        return prev[b.Length];
    }

    // ------------------------------------------------------------------ data

    private void EnsureLoaded()
    {
        if (_titles is not null) return;
        lock (_lock)
        {
            if (_titles is not null) return;
            using var conn = Open();

            // The printed title is the face name for multi-face cards (e.g. "Delver of Secrets"), else the name.
            var rows = conn.Query<(string Title, string Name)>("""
                SELECT DISTINCT COALESCE(faceName, name), name FROM cards
                WHERE side IS NULL OR side = 'a'
                UNION
                SELECT DISTINCT name, name FROM cards WHERE side IS NULL OR side = 'a'
                """);
            var titles = rows
                .Select(r => new TitleEntry(Normalize(r.Title), r.Name))
                .Where(t => t.Normalized.Length > 0)
                .DistinctBy(t => (t.Normalized, t.Name))
                .ToArray();

            _setCodes = conn.Query<string>("SELECT code FROM sets").ToHashSet(StringComparer.OrdinalIgnoreCase);
            _titles = titles;
        }
    }
}
