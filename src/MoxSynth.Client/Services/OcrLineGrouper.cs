using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

/// <summary>
/// Joins OCR words (boxes) into text lines: words whose vertical centres are close belong to one line, unless
/// the horizontal gap is large (two columns, e.g. the set code on the left and the copyright on the right).
/// Works for word-level and line-level input alike.
/// </summary>
public static class OcrLineGrouper
{
    public static IReadOnlyList<OcrLineDto> Group(IEnumerable<OcrLineDto> words)
    {
        var lines = new List<List<OcrLineDto>>();
        foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Height > 0)
                     .OrderBy(w => w.Y + w.Height / 2).ThenBy(w => w.X))
        {
            var centre = word.Y + word.Height / 2;
            var line = lines.FirstOrDefault(l =>
            {
                var last = l[^1];
                var lineCentre = l.Average(w => w.Y + w.Height / 2);
                var lineHeight = l.Average(w => w.Height);
                if (Math.Abs(centre - lineCentre) > Math.Max(lineHeight, word.Height) * 0.5) return false;
                // Must be next to some word of the line (not far away in another column).
                return l.Any(w => HorizontalGap(w, word) <= Math.Max(lineHeight, word.Height) * 3);
            });
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        return lines.Select(l =>
        {
            var ordered = l.OrderBy(w => w.X).ToList();
            var x = ordered.Min(w => w.X);
            var y = ordered.Min(w => w.Y);
            var right = ordered.Max(w => w.X + w.Width);
            var bottom = ordered.Max(w => w.Y + w.Height);
            return new OcrLineDto(string.Join(' ', ordered.Select(w => w.Text.Trim())), x, y, right - x, bottom - y);
        }).OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
    }

    private static double HorizontalGap(OcrLineDto a, OcrLineDto b) =>
        Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.X + a.Width, b.X + b.Width));
}
