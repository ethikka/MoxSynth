using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoxSynth.Cards;
using MoxSynth.Collection;

namespace MoxSynth.Decks;

/// <summary>A friendly, user-facing problem with an Archidekt import (bad URL, deck not found, Archidekt unreachable).</summary>
public sealed class ArchidektImportException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Result of an Archidekt import: the deck it went into plus what was added, warned about and skipped.</summary>
public sealed record ArchidektImportResult(long DeckId, string DeckName, DeckImportResult Import);

/// <summary>
/// Imports a public deck from Archidekt (<c>https://archidekt.com/decks/{id}/{slug}</c>) through its public JSON API
/// (<c>/api/decks/{id}/</c>), either as a new deck (name, format and description from Archidekt) or into an existing one.
/// <para>Categories: Archidekt's deck categories are ensured with <c>countsInDeck = includedInDeck</c>; "Commander" (or the
/// premier category), "Sideboard" and "Maybeboard" go to the built-in categories (by name, case-insensitive) and keep
/// their own setting. A card keeps all its categories, the first being primary.</para>
/// <para>Printings: by Scryfall id, else set code + collector number (when the name matches), else by name (newest
/// regular printing, with a warning). Foil and Etched modifiers become the finish.</para>
/// Registered as a typed <see cref="HttpClient"/> (15 s timeout, generic User-Agent).
/// </summary>
public sealed partial class ArchidektDeckImport(HttpClient http, DeckRepository decks, CardIndex index, ILogger<ArchidektDeckImport> logger)
{
    public const string UserAgent = "MoxSynth/1.0";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int MaxDeckNameLength = 200;

    /// <summary>
    /// Archidekt's <c>deckFormat</c> numbers → MoxSynth format keys. Confirmed against Archidekt's deck search
    /// (<c>/api/decks/v3/?deckFormat=N</c>, by the names and sizes of the top decks). Numbers without an MTGJSON
    /// legality (Custom, Frontier, 1v1 Commander, Explorer, Canadian Highlander, Tiny Leaders, …) or not listed → casual.
    /// </summary>
    private static readonly Dictionary<int, string> FormatMap = new()
    {
        [1] = "standard",
        [2] = "modern",
        [3] = "commander",
        [4] = "legacy",
        [5] = "vintage",
        [6] = "pauper",
        // 7 = Custom, 8 = Frontier → casual
        [9] = "future",            // Future Standard
        [10] = "penny",            // Penny Dreadful
        // 11 = 1v1 Commander → casual (no MTGJSON legality)
        [12] = "duel",             // Duel Commander
        [13] = "standardbrawl",    // Brawl (60 cards, Standard card pool)
        [14] = "oathbreaker",
        [15] = "pioneer",
        [16] = "historic",
        [17] = "paupercommander",  // Pauper EDH
        [18] = "alchemy",
        // 19 = Explorer → casual
        [20] = "brawl",            // Historic Brawl (100 cards) = MTGJSON "brawl"
        [21] = "gladiator",
        [22] = "premodern",
        [23] = "predh",
        [24] = "timeless",
        // 25 = Canadian Highlander → casual
        [26] = "competitivebrawl",
        // 27 = Tiny Leaders → casual
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ================================================================== public API

    /// <summary>Creates a new deck from an Archidekt URL (or bare id). On failure nothing is left behind.</summary>
    public async Task<ArchidektImportResult> ImportNewAsync(string urlOrId, CancellationToken ct = default)
    {
        var id = ParseDeckId(urlOrId);
        var deck = await FetchAsync(id, ct);
        var formats = await decks.GetFormatsAsync(ct);
        var format = MapFormat(deck.DeckFormat, formats);
        var name = DeckName(deck, id);

        var deckId = await decks.CreateAsync(name, format, PlainDescription(deck.Description), ct);
        try
        {
            var result = await ImportCardsAsync(deckId, deck, replace: false, ct);
            return new ArchidektImportResult(deckId, name, result);
        }
        catch
        {
            try { await decks.DeleteAsync(deckId, CancellationToken.None); }
            catch (Exception cleanup) { logger.LogWarning(cleanup, "Couldn't remove deck {DeckId} after a failed Archidekt import.", deckId); }
            throw;
        }
    }

    /// <summary>Imports the cards of an Archidekt deck into an existing deck (name, format and description stay).</summary>
    public async Task<ArchidektImportResult> ImportIntoAsync(long deckId, string urlOrId, bool replace, CancellationToken ct = default)
    {
        var id = ParseDeckId(urlOrId);
        var deck = await FetchAsync(id, ct);
        var result = await ImportCardsAsync(deckId, deck, replace, ct);
        return new ArchidektImportResult(deckId, DeckName(deck, id), result);
    }

    /// <summary>
    /// The deck id of <c>archidekt.com/decks/{id}[/slug]</c> (with or without scheme or <c>www.</c>) or of a bare number.
    /// Throws <see cref="ArchidektImportException"/> for anything else.
    /// </summary>
    public static long ParseDeckId(string? urlOrId)
    {
        const string help = "Enter an Archidekt deck link, such as https://archidekt.com/decks/123456/my-deck.";
        var s = (urlOrId ?? "").Trim();
        if (s.Length == 0) throw new ArchidektImportException(help);
        if (s.All(char.IsAsciiDigit))
            return long.TryParse(s, out var bare) && bare > 0 ? bare : throw new ArchidektImportException(help);

        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArchidektImportException(help);
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("archidekt.com" or "www.archidekt.com"))
            throw new ArchidektImportException("That isn't an Archidekt link. " + help);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("decks", StringComparison.OrdinalIgnoreCase) &&
            segments[1].All(char.IsAsciiDigit) && long.TryParse(segments[1], out var id) && id > 0)
            return id;
        throw new ArchidektImportException("That Archidekt link doesn't point to a deck. " + help);
    }

    /// <summary>Archidekt <c>deckFormat</c> → a MoxSynth format key from <paramref name="available"/>, else casual.</summary>
    public static string MapFormat(int? deckFormat, IReadOnlyCollection<string> available) =>
        deckFormat is { } n && FormatMap.TryGetValue(n, out var key) && available.Contains(key) ? key : DeckFormats.Casual;

    /// <summary>Archidekt descriptions are Quill deltas (<c>{"ops":[{"insert":"…"}]}</c>); this returns their plain text.</summary>
    public static string? PlainDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var s = description.Trim();
        if (s.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var op in ops.EnumerateArray())
                        if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("insert", out var insert) &&
                            insert.ValueKind == JsonValueKind.String)
                            sb.Append(insert.GetString());
                    s = sb.ToString();
                }
            }
            catch (JsonException)
            {
                // not a delta after all: keep the text as it is
            }
        }
        s = ExtraBlankLinesRegex().Replace(s.Replace("\r\n", "\n"), "\n\n").Trim();
        return s.Length == 0 ? null : s;
    }

    // ================================================================== fetch

    private async Task<ArchidektDeck> FetchAsync(long id, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://archidekt.com/api/decks/{id}/", HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ArchidektImportException("Archidekt didn't answer in time. Try again in a moment.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Archidekt request for deck {Id} failed.", id);
            throw new ArchidektImportException("Couldn't reach Archidekt. Check your internet connection and try again.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    throw new ArchidektImportException("This deck doesn't exist or is private.");
                case HttpStatusCode.TooManyRequests:
                    throw new ArchidektImportException("Archidekt is receiving too many requests. Wait a minute and try again.");
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Archidekt returned {Status} for deck {Id}.", (int)response.StatusCode, id);
                throw new ArchidektImportException($"Archidekt couldn't send the deck (HTTP {(int)response.StatusCode}). Try again later.");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var deck = await JsonSerializer.DeserializeAsync<ArchidektDeck>(stream, JsonOptions, ct);
                if (deck?.Cards is null) throw new JsonException("No cards array.");
                return deck;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Unexpected Archidekt response for deck {Id}.", id);
                throw new ArchidektImportException("Archidekt sent a response MoxSynth doesn't understand. Its API may have changed.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                       ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ArchidektImportException("The connection to Archidekt was interrupted. Try again.", ex);
            }
        }
    }

    // ================================================================== mapping

    private async Task<DeckImportResult> ImportCardsAsync(long deckId, ArchidektDeck deck, bool replace, CancellationToken ct)
    {
        var premier = (deck.Categories ?? [])
            .Where(c => c.IsPremier && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string MapCategory(string name) => premier.Contains(name) ? "Commander" : name;

        var categories = (deck.Categories ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new DeckTextCategory(MapCategory(c.Name!.Trim()), c.IncludedInDeck))
            .ToList();

        var cards = new List<DeckImportCard>();
        var unmatched = new List<DeckImportLine>();
        var warnings = new List<DeckImportLine>();
        var line = 0;
        foreach (var entry in deck.Cards!)
        {
            if (entry.DeletedAt is not null || entry.Quantity < 1) continue;
            line++;
            var card = entry.Card;
            var name = card?.OracleCard?.Name?.Trim() ?? card?.DisplayName?.Trim() ?? "";
            var set = card?.Edition?.EditionCode?.Trim();
            var number = card?.CollectorNumber?.Trim();
            var finish = entry.Modifier?.Trim().ToLowerInvariant() switch
            {
                "foil" => CardFinishes.Foil,
                "etched" => CardFinishes.Etched,
                _ => CardFinishes.NonFoil,
            };
            var text = DescribeLine(entry.Quantity, name, set, number, finish);

            var (printing, warning) = Resolve(card?.Uid, set, number, name, finish);
            if (printing is null)
            {
                unmatched.Add(new DeckImportLine(line, text, name.Length == 0 ? "No card name." : $"Unknown card “{name}”."));
                continue;
            }
            if (warning is not null) warnings.Add(new DeckImportLine(line, text, warning));

            var cardCategories = (entry.Categories ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => MapCategory(c.Trim()))
                .ToList();
            cards.Add(new DeckImportCard(line, text, printing.Uuid, entry.Quantity, finish, cardCategories));
        }

        foreach (var custom in deck.CustomCards ?? [])
        {
            line++;
            var name = custom.ValueKind == JsonValueKind.Object && custom.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "Custom card"
                : "Custom card";
            unmatched.Add(new DeckImportLine(line, name, "Custom cards can't be imported."));
        }

        var result = await decks.ImportCardsAsync(deckId, categories, cards, replace, ct);
        return new DeckImportResult
        {
            Added = result.Added,
            Unmatched = unmatched.Concat(result.Unmatched).OrderBy(u => u.Line).ToList(),
            Warnings = warnings.Concat(result.Warnings).OrderBy(w => w.Line).ToList(),
        };
    }

    /// <summary>Scryfall id → set + collector number (if the name matches) → name (newest regular printing).</summary>
    private (CardRef? Card, string? Warning) Resolve(string? scryfallId, string? set, string? number, string name, string finish)
    {
        if (index.ByScryfallId(scryfallId) is { } byId) return (byId, null);

        var printings = index.ByName(name);
        if (index.BySetAndNumber(set, number) is { } exact &&
            (printings.Any(p => p.Uuid == exact.Uuid) || exact.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (exact, null);
        if (printings.Count == 0) return (null, null);

        var inSet = set is null ? [] : printings.Where(p => p.SetCode.Equals(set, StringComparison.OrdinalIgnoreCase)).ToList();
        return inSet.Count > 0
            ? (ArchidektImporter.BestPrinting(inSet, finish), $"Printing not found; used another printing from {set!.ToLowerInvariant()}.")
            : (ArchidektImporter.BestPrinting(printings, finish), "Printing not found; used the newest regular printing.");
    }

    private static string DescribeLine(int quantity, string name, string? set, string? number, string finish)
    {
        var sb = new StringBuilder().Append(quantity).Append("x ").Append(name.Length == 0 ? "(unnamed card)" : name);
        if (!string.IsNullOrEmpty(set)) sb.Append(" (").Append(set.ToLowerInvariant()).Append(')');
        if (!string.IsNullOrEmpty(number)) sb.Append(' ').Append(number);
        if (finish == CardFinishes.Foil) sb.Append(" *F*");
        else if (finish == CardFinishes.Etched) sb.Append(" *E*");
        return sb.ToString();
    }

    private static string DeckName(ArchidektDeck deck, long id)
    {
        var name = (deck.Name ?? "").Trim();
        if (name.Length == 0) name = $"Archidekt deck {id}";
        return name.Length > MaxDeckNameLength ? name[..MaxDeckNameLength].Trim() : name;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();

    // ================================================================== Archidekt JSON (only what is used)

    private sealed class ArchidektDeck
    {
        public string? Name { get; set; }
        public int? DeckFormat { get; set; }
        public string? Description { get; set; }
        public List<ArchidektCategory>? Categories { get; set; }
        public List<ArchidektCardEntry>? Cards { get; set; }
        public List<JsonElement>? CustomCards { get; set; }
    }

    private sealed class ArchidektCategory
    {
        public string? Name { get; set; }
        public bool IncludedInDeck { get; set; } = true;
        public bool IsPremier { get; set; }
    }

    private sealed class ArchidektCardEntry
    {
        public int Quantity { get; set; }
        public List<string>? Categories { get; set; }
        public string? Modifier { get; set; }
        public string? DeletedAt { get; set; }
        public ArchidektCard? Card { get; set; }
    }

    private sealed class ArchidektCard
    {
        public string? Uid { get; set; }
        public string? CollectorNumber { get; set; }
        public string? DisplayName { get; set; }
        public ArchidektEdition? Edition { get; set; }
        public ArchidektOracleCard? OracleCard { get; set; }
    }

    private sealed class ArchidektEdition
    {
        [JsonPropertyName("editioncode")]
        public string? EditionCode { get; set; }
    }

    private sealed class ArchidektOracleCard
    {
        public string? Name { get; set; }
    }
}
