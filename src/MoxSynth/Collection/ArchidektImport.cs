using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MoxSynth.Cards;
using MoxSynth.Infrastructure;

namespace MoxSynth.Collection;

/// <summary>One line of an import file, with the card it was matched to (or the reason it wasn't).</summary>
public sealed class ImportRow
{
    public int Line { get; init; }
    public int Quantity { get; init; }
    public string Name { get; init; } = "";
    public string? SetCode { get; init; }
    public string? Number { get; init; }
    public string Finish { get; set; } = CardFinishes.NonFoil;
    public CardCondition Condition { get; init; } = CardCondition.NearMint;
    public string? ConditionText { get; init; }
    public string Language { get; init; } = "English";
    public double? PurchasePrice { get; init; }
    public DateTime? DateAdded { get; init; }
    public string? Tags { get; init; }

    public CardRef? Card { get; set; }
    /// <summary>How the card was found: "Scryfall ID", "set + number", "name + set", "name".</summary>
    public string? MatchedBy { get; set; }
    public string? Problem { get; set; }

    public bool IsMatched => Card is not null;
}

public sealed class ImportPreview
{
    public required string FileName { get; init; }
    public required string FileHash { get; init; }
    public required IReadOnlyList<ImportRow> Rows { get; init; }
    /// <summary>Problems with the file as a whole (e.g. missing columns).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> RecognisedColumns { get; init; } = [];

    public IEnumerable<ImportRow> Matched => Rows.Where(r => r.IsMatched);
    public IEnumerable<ImportRow> Unmatched => Rows.Where(r => !r.IsMatched);
    public int MatchedCards => Matched.Sum(r => r.Quantity);
    public int UnmatchedCards => Unmatched.Sum(r => r.Quantity);
}

/// <summary>
/// Reads an Archidekt collection export (CSV). Archidekt lets you choose the exported columns, so headers are
/// matched loosely (case/spacing-insensitive, with aliases); only a card name or Scryfall ID is required.
/// </summary>
public sealed class ArchidektImporter(CardIndex index)
{
    private static readonly Dictionary<string, string[]> ColumnAliases = new()
    {
        ["quantity"] = ["quantity", "qty", "count", "amount"],
        ["name"] = ["name", "card name", "cardname", "card"],
        ["finish"] = ["finish", "foil", "printing", "variant"],
        ["condition"] = ["condition", "cond"],
        ["dateadded"] = ["date added", "dateadded", "added", "date"],
        ["language"] = ["language", "lang"],
        ["price"] = ["purchase price", "purchaseprice", "price paid", "buy price"],
        ["tags"] = ["tags", "tag", "categories", "category"],
        ["setcode"] = ["edition code", "editioncode", "set code", "setcode", "set", "edition"],
        ["scryfallid"] = ["scryfall id", "scryfallid", "scryfall uuid", "scryfall_id"],
        ["number"] = ["collector number", "collectornumber", "collector no", "number", "cn"],
    };

    public ImportPreview Parse(string fileName, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var text = Encoding.UTF8.GetString(content);
        var table = CsvReader.Parse(text);

        if (table.Count < 2)
            return new ImportPreview { FileName = fileName, FileHash = hash, Rows = [], Errors = ["The file contains no card rows."] };

        var columns = MapColumns(table[0]);
        if (!columns.ContainsKey("name") && !columns.ContainsKey("scryfallid"))
        {
            return new ImportPreview
            {
                FileName = fileName,
                FileHash = hash,
                Rows = [],
                Errors = ["This doesn't look like an Archidekt collection export: no “Name” or “Scryfall ID” column found. " +
                          $"Columns in the file: {string.Join(", ", table[0])}."],
            };
        }

        string? Get(string[] row, string key) =>
            columns.TryGetValue(key, out var i) && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

        var rows = new List<ImportRow>();
        for (var line = 1; line < table.Count; line++)
        {
            var r = table[line];
            var quantityText = Get(r, "quantity");
            var quantity = quantityText is null ? 1 : int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q : 0;
            var conditionText = Get(r, "condition");

            var row = new ImportRow
            {
                Line = line + 1,
                Quantity = quantity,
                Name = Get(r, "name") ?? "",
                SetCode = Get(r, "setcode"),
                Number = Get(r, "number"),
                Finish = ParseFinish(Get(r, "finish")),
                Condition = ParseCondition(conditionText),
                ConditionText = conditionText,
                Language = ParseLanguage(Get(r, "language")),
                PurchasePrice = ParsePrice(Get(r, "price")),
                DateAdded = ParseDate(Get(r, "dateadded")),
                Tags = Get(r, "tags"),
            };

            if (quantity <= 0)
                row.Problem = $"Invalid quantity “{quantityText}”.";
            else if (quantity > 1000)
                row.Problem = "Quantity above 1000 – skipped to be safe.";
            else
                Match(row, Get(r, "scryfallid"));

            rows.Add(row);
        }

        return new ImportPreview
        {
            FileName = fileName,
            FileHash = hash,
            Rows = rows,
            RecognisedColumns = columns.Keys.ToList(),
        };
    }

    private void Match(ImportRow row, string? scryfallId)
    {
        if (index.ByScryfallId(scryfallId) is { } byId)
        {
            Accept(row, byId, "Scryfall ID");
            return;
        }
        if (index.BySetAndNumber(row.SetCode, row.Number) is { } bySetNumber)
        {
            Accept(row, bySetNumber, "set + number");
            return;
        }

        var printings = index.ByName(row.Name);
        if (printings.Count == 0)
        {
            row.Problem = string.IsNullOrEmpty(row.Name) ? "No card name or known Scryfall ID." : $"Unknown card “{row.Name}”.";
            return;
        }

        var inSet = row.SetCode is null ? [] : printings.Where(p => p.SetCode.Equals(row.SetCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inSet.Count > 0)
            Accept(row, BestPrinting(inSet, row.Finish), "name + set");
        else
            Accept(row, BestPrinting(printings, row.Finish),
                row.SetCode is null ? "name (newest regular printing)" : $"name (set “{row.SetCode}” not found, newest regular printing)");
    }

    /// <summary>
    /// Without an exact printing: prefer regular paper printings (no promos/masterpieces/online-only) that exist in the
    /// requested finish, newest first (the list is already sorted newest first).
    /// </summary>
    internal static CardRef BestPrinting(IReadOnlyList<CardRef> printings, string finish) =>
        printings
            .OrderByDescending(p => p.IsRegularPaper)
            .ThenByDescending(p => p.IsPaper)
            .ThenByDescending(p => p.FinishList.Contains(finish))
            .First(); // OrderBy is stable, so newest-first is kept within each group

    private static void Accept(ImportRow row, CardRef card, string matchedBy)
    {
        row.Card = card;
        row.MatchedBy = matchedBy;
        // Keep the finish only if this printing exists in it (e.g. "foil" for a non-foil-only printing → non-foil).
        if (!card.FinishList.Contains(row.Finish)) row.Finish = card.FinishList[0];
    }

    // ------------------------------------------------------------------ value parsing

    private static Dictionary<string, int> MapColumns(string[] header)
    {
        static string Normalize(string s) => new(s.Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray());

        var map = new Dictionary<string, int>();
        for (var i = 0; i < header.Length; i++)
        {
            var h = Normalize(header[i]);
            foreach (var (key, aliases) in ColumnAliases)
            {
                if (!map.ContainsKey(key) && aliases.Any(a => a == h || a.Replace(" ", "") == h.Replace(" ", "")))
                {
                    map[key] = i;
                    break;
                }
            }
        }
        // "Edition" can mean the edition name; prefer an explicit code column when both exist.
        return map;
    }

    internal static string ParseFinish(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "normal" or "nonfoil" or "non-foil" or "regular" or "false" or "no" or "0" => CardFinishes.NonFoil,
        "etched" or "etched foil" or "foil etched" => CardFinishes.Etched,
        var v when v.Contains("etched") => CardFinishes.Etched,
        var v when v.Contains("foil") || v is "true" or "yes" or "1" => CardFinishes.Foil,
        _ => CardFinishes.NonFoil,
    };

    /// <summary>Archidekt uses TCGplayer-style grades; mapped onto the Cardmarket scale used by MoxSynth.</summary>
    internal static CardCondition ParseCondition(string? value)
    {
        var v = new string((value ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
        return v switch
        {
            "m" or "mint" or "mt" => CardCondition.Mint,
            "" or "nm" or "nearmint" => CardCondition.NearMint,
            "lp" or "lightlyplayed" or "sp" or "slightlyplayed" or "ex" or "excellent" => CardCondition.Excellent,
            "mp" or "moderatelyplayed" or "gd" or "good" => CardCondition.Good,
            "pl" or "played" => CardCondition.Played,
            "hp" or "heavilyplayed" => CardCondition.Played,
            "d" or "dmg" or "damaged" or "po" or "poor" => CardCondition.Poor,
            _ => CardCondition.NearMint,
        };
    }

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["de"] = "German", ["fr"] = "French", ["it"] = "Italian", ["es"] = "Spanish",
        ["pt"] = "Portuguese (Brazil)", ["ja"] = "Japanese", ["jp"] = "Japanese", ["ko"] = "Korean", ["kr"] = "Korean",
        ["ru"] = "Russian", ["zhs"] = "Chinese Simplified", ["cs"] = "Chinese Simplified",
        ["zht"] = "Chinese Traditional", ["ct"] = "Chinese Traditional", ["ph"] = "Phyrexian",
    };

    internal static string ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "English";
        return Languages.TryGetValue(value.Trim(), out var name)
            ? name
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());
    }

    private static double? ParsePrice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray());
        // "1,50" (European decimal comma) vs "1,234.50" (thousands separator).
        if (cleaned.Contains(',') && !cleaned.Contains('.')) cleaned = cleaned.Replace(',', '.');
        else cleaned = cleaned.Replace(",", "");
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price > 0 ? price : null;
    }

    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        string[] formats =
        [
            "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "dd-MM-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "M/d/yyyy", "d-M-yyyy",
        ];
        // Values without a time zone (e.g. "2023-05-12") are taken as local time; explicit offsets/Z are respected.
        const DateTimeStyles styles = DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;
        if (DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, styles, out var exact))
            return exact;
        return DateTime.TryParse(v, CultureInfo.InvariantCulture, styles, out var any) ? any : null;
    }
}
