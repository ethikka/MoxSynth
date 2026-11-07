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
