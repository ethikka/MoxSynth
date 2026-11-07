namespace MoxSynth.Cards;

/// <summary>One printing (MTGJSON uuid) as shown in search results and lists.</summary>
public sealed class CardSummary
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string SetCode { get; set; } = "";
    public string? SetName { get; set; }
    public string? KeyruneCode { get; set; }
    public string? ReleaseDate { get; set; }
    public string Number { get; set; } = "";
    public string? Rarity { get; set; }
    public string? ManaCost { get; set; }
    public string? Type { get; set; }
    public string? Finishes { get; set; }
    public string? Language { get; set; }
    public string? Layout { get; set; }
    public string? ScryfallId { get; set; }

    public IReadOnlyList<string> FinishList => CardFinishes.Parse(Finishes);
}

/// <summary>A single face row from the cards table (multi-face cards have one row per side).</summary>
public sealed class CardFace
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FaceName { get; set; }
    public string? Side { get; set; }
    public string? ManaCost { get; set; }
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? FlavorText { get; set; }
    public string? Power { get; set; }
    public string? Toughness { get; set; }
    public string? Loyalty { get; set; }
    public string? Defense { get; set; }
    public string? Artist { get; set; }
    public string? Rarity { get; set; }
    public string SetCode { get; set; } = "";
    public string? SetName { get; set; }
    public string? KeyruneCode { get; set; }
    public string? ReleaseDate { get; set; }
    public string Number { get; set; } = "";
    public string? Finishes { get; set; }
    public string? Layout { get; set; }
    public string? Language { get; set; }
    public string? OtherFaceIds { get; set; }
    public string? ScryfallId { get; set; }

    public string DisplayName => FaceName ?? Name;
}

public sealed class CardDetails
{
    /// <summary>The uuid that represents the physical card (side "a" for multi-face cards).</summary>
    public required CardFace Primary { get; init; }
    public required IReadOnlyList<CardFace> Faces { get; init; }
    public required IReadOnlyList<CardSummary> OtherPrintings { get; init; }

    public string Uuid => Primary.Uuid;
    public string Name => Primary.Name;
    public IReadOnlyList<string> FinishList => CardFinishes.Parse(Primary.Finishes);

    public bool HasBackImage => Primary.Layout is "transform" or "modal_dfc" or "reversible_card" or "double_faced_token";
}

public sealed class SetInfo
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ReleaseDate { get; set; }
    public string? Type { get; set; }
    public string? KeyruneCode { get; set; }

    public string Label => $"{Name} ({Code}{(ReleaseDate is { Length: >= 4 } ? ", " + ReleaseDate[..4] : "")})";
}

public sealed class CardDbMeta
{
    public string? Date { get; set; }
    public string? Version { get; set; }
}

public sealed class CardSearchQuery
{
    public string? Name { get; set; }
    public string? Text { get; set; }
    public string? Type { get; set; }
    public string? SetCode { get; set; }
    public string? Rarity { get; set; }
    /// <summary>Any of W,U,B,R,G (card must contain all selected) or C for colourless.</summary>
    public string? Colors { get; set; }
    public bool IncludeNonPaper { get; set; }
    /// <summary>Optional Scryfall-syntax query (see <see cref="ScryfallQueryParser"/>), combined with the other filters.</summary>
    public string? Scryfall { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>The name box, split into card-name words and an optional collector number.</summary>
    public NameSearch NameSearch => NameSearch.Parse(Name);

    public bool HasCollectorNumber => NameSearch.CollectorNumber is not null;

    public bool HasCriteria => HasCriteriaBesidesScryfall || !string.IsNullOrWhiteSpace(Scryfall);

    public bool HasCriteriaBesidesScryfall =>
        !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(Type) ||
        !string.IsNullOrWhiteSpace(SetCode) || !string.IsNullOrWhiteSpace(Rarity) || !string.IsNullOrWhiteSpace(Colors);
}

/// <summary>
/// Parsed contents of the name box. Words are separated by spaces; a word that starts with a digit
/// ("12", "113p") is a collector number, every other word must occur somewhere in the card name (any order).
/// "bolt light" → Lightning Bolt; "bolt 12" → cards with "bolt" in the name and a collector number starting with 12.
/// </summary>
public sealed record NameSearch(IReadOnlyList<string> NameParts, string? CollectorNumber)
{
    public bool IsEmpty => NameParts.Count == 0 && CollectorNumber is null;

    /// <summary>The name words as typed, joined back together – used to rank exact/prefix matches first.</summary>
    public string Phrase => string.Join(' ', NameParts);

    public static NameSearch Parse(string? input)
    {
        var parts = new List<string>();
        string? number = null;
        foreach (var word in (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsCollectorNumber(word))
                number ??= word; // a card has one collector number; extra numbers are ignored
            else
                parts.Add(word);
        }
        return new NameSearch(parts, number);
    }

    private static bool IsCollectorNumber(string word) =>
        word.Length <= 10 && char.IsAsciiDigit(word[0]) && word.All(char.IsAsciiLetterOrDigit);
}

public static class CardFinishes
{
    public const string NonFoil = "nonfoil";
    public const string Foil = "foil";
    public const string Etched = "etched";

    public static IReadOnlyList<string> Parse(string? finishes) =>
        string.IsNullOrWhiteSpace(finishes)
            ? [NonFoil]
            : finishes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string Label(string finish) => finish switch
    {
        NonFoil => "Non-foil",
        Foil => "Foil",
        Etched => "Etched",
        "signed" => "Signed",
        _ => finish,
    };

    public static string Short(string finish) => finish switch
    {
        NonFoil => "NF",
        Foil => "F",
        Etched => "E",
        "signed" => "S",
        _ => finish,
    };
}

public static class ScryfallImages
{
    /// <summary>size: small | normal | large | png | art_crop | border_crop</summary>
    public static string? Url(string? scryfallId, string size = "normal", bool back = false)
    {
        if (string.IsNullOrEmpty(scryfallId) || scryfallId.Length < 2) return null;
        var face = back ? "back" : "front";
        return $"https://cards.scryfall.io/{size}/{face}/{scryfallId[0]}/{scryfallId[1]}/{scryfallId}.jpg";
    }
}
