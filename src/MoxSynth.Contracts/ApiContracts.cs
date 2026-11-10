namespace MoxSynth.Contracts;

// JSON contracts of the MoxSynth API. All routes live under /api; everything except /api/profiles and /api/auth/*
// needs "Authorization: Bearer <accessToken>". JSON is camelCase, timestamps are UTC (ISO 8601).

// ---------------------------------------------------------------- profiles & sign-in

public sealed record ProfileDto(long Id, string Name, string Color, string Initial, bool IsAdmin, bool HasPassword);

public sealed record LoginRequest(long ProfileId, string Password);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>Same shape as ASP.NET Core's bearer token response (expiresIn in seconds).</summary>
public sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

/// <summary>Error body of non-2xx responses. Codes: see <see cref="ApiErrorCodes"/>.</summary>
public sealed record ApiError(string Code, string Message);

public static class ApiErrorCodes
{
    public const string WrongPassword = "wrong-password";
    public const string LockedOut = "locked";
    public const string NoPassword = "no-password";
    public const string NotFound = "not-found";
    public const string Unauthorized = "unauthorized";
    public const string Invalid = "invalid";
    /// <summary>The request breaks a rule (e.g. deleting the Sideboard category).</summary>
    public const string Conflict = "conflict";
}

/// <summary>GET /api/me – the signed-in profile and server facts.</summary>
public sealed record MeDto(
    ProfileDto Profile,
    IReadOnlyList<PriceProviderDto> PriceProviders,
    string DefaultPriceProvider,
    string? CardDatabaseDate,
    string? PricesDate);

public sealed record PriceProviderDto(string Id, string Label, string Currency)
{
    public override string ToString() => Label; // pickers
}

// ---------------------------------------------------------------- cards

public sealed record PageDto<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasMore => Page < TotalPages;
}

public sealed record SetDto(string Code, string Name, string? ReleaseDate, string? KeyruneCode);

public sealed record CardSummaryDto(
    string Uuid,
    string Name,
    string SetCode,
    string? SetName,
    string Number,
    string? Rarity,
    string? ManaCost,
    string? Type,
    IReadOnlyList<string> Finishes,
    string? Language,
    string? ImageUrl,
    string? SmallImageUrl,
    long Owned,
    /// <summary>Prices at the requested provider, one per finish (main finish first). Empty without prices.</summary>
    IReadOnlyList<PriceDto> Prices);

public sealed record CardFaceDto(
    string Name,
    string? ManaCost,
    string? Type,
    string? Text,
    string? FlavorText,
    string? Power,
    string? Toughness,
    string? Loyalty,
    string? Defense,
    string? Artist);

/// <summary>GET /api/cards/{uuid}</summary>
public sealed record CardDetailDto(
    string Uuid,
    string Name,
    string SetCode,
    string? SetName,
    string Number,
    string? Rarity,
    string? Language,
    string? ReleaseDate,
    IReadOnlyList<string> Finishes,
    string? ImageUrl,
    string? BackImageUrl,
    IReadOnlyList<CardFaceDto> Faces,
    IReadOnlyList<CardSummaryDto> OtherPrintings,
    /// <summary>All providers × finishes, with stats and history.</summary>
    IReadOnlyList<PriceDto> Prices,
    /// <summary>Owned copies first, then removed ones.</summary>
    IReadOnlyList<CopyDto> Copies);

// ---------------------------------------------------------------- prices

public sealed record PriceDto(
    string Provider,
    string Finish,
    string Currency,
    double Price,
    double? Change7d,
    double? Change30d,
    double? ChangeFirst,
    string FirstDate,
    string LastDate,
    bool IsStale,
    /// <summary>Daily prices FirstDate..LastDate (null = no price that day); omitted where not needed.</summary>
    IReadOnlyList<double?>? History,
    PriceStatsDto? Stats);

public sealed record PriceStatsDto(double Low, string LowDate, double High, string HighDate, double Average, int DaysWithPrice);

// ---------------------------------------------------------------- collection

public sealed record CopyDto(
    long Id,
    string Finish,
    string Condition,
    string Language,
    double? PurchasePrice,
    string? Notes,
    DateTime AddedAt,
    DateTime? RemovedAt,
    string? RemovalReason);

/// <summary>POST /api/collection – add copies of a printing (name/set/number are taken from the catalogue).</summary>
public sealed record AddCardsDto(
    string CardUuid,
    string Finish = "nonfoil",
    string Condition = "NearMint",
    string? Language = null,
    int Quantity = 1,
    double? PurchasePrice = null,
    string? Notes = null);

public sealed record AddCardsResultDto(IReadOnlyList<long> Ids, long Owned);

/// <summary>POST /api/collection/remove-one – removes the oldest owned copy of that group.</summary>
public sealed record RemoveOneDto(string CardUuid, string Finish, string Condition, string Language, string? Reason = null);

public sealed record CollectionGroupDto(
    string CardUuid,
    string Name,
    string SetCode,
    string Number,
    string Finish,
    string Condition,
    string Language,
    long Quantity,
    DateTime FirstAddedAt,
    DateTime LastAddedAt,
    string? ManaCost,
    string? SmallImageUrl,
    PriceDto? Price,
    double? Value,
    /// <summary>A group of removed copies (only with ?removed=true).</summary>
    bool IsRemoved = false,
    DateTime? LastRemovedAt = null,
    /// <summary>Distinct removal reasons, e.g. "Sold, Traded".</summary>
    string? RemovalReasons = null);

public sealed record CollectionPageDto(
    PageDto<CollectionGroupDto> Groups,
    long TotalCopies,
    double? TotalValue,
    string? Currency,
    long UnpricedCopies,
    /// <summary>Removed copies among the groups (0 unless ?removed=true). TotalCopies/TotalValue are owned copies only.</summary>
    long RemovedCopies = 0);

/// <summary>GET /api/stats</summary>
public sealed record StatsDto(
    long OwnedCopies,
    long UniquePrintings,
    long UniqueNames,
    long RemovedCopies,
    long AddedLast30Days,
    long RemovedLast30Days,
    double? Value,
    string? Currency,
    long UnpricedCopies);

public sealed record HistoryEventDto(
    long Id,
    string CardUuid,
    string Name,
    string SetCode,
    string Number,
    string Finish,
    string Condition,
    /// <summary>"Added" or "Removed".</summary>
    string Kind,
    string? RemovalReason,
    DateTime At);

// ---------------------------------------------------------------- scanning

/// <summary>A line of OCR text with its bounding box in image pixels.</summary>
public sealed record OcrLineDto(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// POST /api/cards/recognize. Either the OCR lines of a whole photo (the server finds the title and the
/// bottom-left info line from their positions) or the already cropped title / bottom texts.
/// </summary>
public sealed record RecognizeRequest(IReadOnlyList<OcrLineDto>? Lines = null, string? Title = null, string? Bottom = null);

public sealed record RecognizeResultDto(
    bool Found,
    string? Name,
    string? SetCode,
    string? Number,
    double Confidence,
    /// <summary>Matching printings (one when the exact printing was read).</summary>
    IReadOnlyList<CardSummaryDto> Candidates,
    string? TitleText,
    string? BottomText);

// ---------------------------------------------------------------- decks

/// <summary>GET /api/decks/formats – deck formats ("casual" plus the MTGJSON legality keys).</summary>
public sealed record DeckFormatDto(
    string Id,
    string Label,
    /// <summary>Has a Commander category, singleton and colour-identity rules.</summary>
    bool IsCommanderStyle,
    /// <summary>Exact deck size (commander included) of a commander-style format, else null.</summary>
    int? CommanderDeckSize)
{
    public override string ToString() => Label; // pickers
}

/// <summary>GET /api/decks – one entry per deck, most recently updated first.</summary>
public sealed record DeckSummaryDto(
    long Id,
    string Name,
    string Format,
    /// <summary>Sum of quantities of cards whose primary category counts in the deck.</summary>
    int CardCount,
    IReadOnlyList<string> CommanderNames,
    string? CoverImageUrl,
    DateTime UpdatedAt,
    /// <summary>Value of the counted cards at the requested provider (null without prices).</summary>
    double? Value,
    string? Currency,
    bool IsLegal);

/// <summary>GET /api/decks/{id} – and the answer of every deck mutation.</summary>
public sealed record DeckDetailDto(
    long Id,
    string Name,
    string Format,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Ordered by position.</summary>
    IReadOnlyList<DeckCategoryDto> Categories,
    /// <summary>Ordered by primary category position, then name.</summary>
    IReadOnlyList<DeckCardDto> Cards,
    DeckLegalityDto Legality,
    double? Value,
    string? Currency,
    /// <summary>Counted cards covered by the collection (per name: min(deck quantity, owned copies of any printing)).</summary>
    int OwnedCount,
    int CardCount,
    IReadOnlyList<string> CommanderNames,
    string? CoverImageUrl);

public sealed record DeckCategoryDto(
    long Id,
    string Name,
    int Position,
    bool CountsInDeck,
    /// <summary>"commander", "sideboard", "maybeboard" for the built-in categories, else null.</summary>
    string? Role,
    /// <summary>Sum of quantities of the cards listed here (whose primary category this is).</summary>
    int CardCount);

public sealed record DeckCardDto(
    long DeckCardId,
    string CardUuid,
    string Name,
    string SetCode,
    string Number,
    string Finish,
    int Quantity,
    /// <summary>Category ids, primary (where the card is listed) first.</summary>
    IReadOnlyList<long> CategoryIds,
    string? ManaCost,
    double ManaValue,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> ColorIdentity,
    string? TypeLine,
    IReadOnlyList<string> Types,
    string? Rarity,
    string? ImageUrl,
    string? SmallImageUrl,
    /// <summary>Unit price for the finish at the requested provider.</summary>
    double? Price,
    /// <summary>Owned copies of this printing in this finish.</summary>
    int OwnedExact,
    /// <summary>Owned copies of any printing with the same name.</summary>
    int OwnedAny,
    /// <summary>Why this card makes the deck illegal, null when fine.</summary>
    string? IllegalReason)
{
    public long? PrimaryCategoryId => CategoryIds.Count > 0 ? CategoryIds[0] : null;
}

public sealed record DeckLegalityDto(
    bool IsLegal,
    /// <summary>Short human-readable problems ("97 of 100 cards", "Sol Ring: banned in Modern").</summary>
    IReadOnlyList<string> Problems,
    /// <summary>Per deck card id: why that card is illegal (only cards with a problem).</summary>
    IReadOnlyDictionary<long, string> CardReasons);

/// <summary>POST /api/decks</summary>
public sealed record CreateDeckRequest(string Name, string Format, string? Description = null);

/// <summary>PUT /api/decks/{id}</summary>
public sealed record UpdateDeckRequest(string Name, string Format, string? Description = null);

/// <summary>
/// POST /api/decks/{id}/cards. <see cref="Category"/> is found by name (case-insensitively; "Sideboard", "Maybeboard",
/// "Commander" also find the renamed built-in one) or created; null = the card's type category.
/// </summary>
public sealed record AddDeckCardRequest(string CardUuid, int Quantity = 1, string Finish = "nonfoil", string? Category = null);

/// <summary>PUT /api/decks/{id}/cards/{deckCardId}/quantity – 0 removes the card.</summary>
public sealed record SetDeckCardQuantityRequest(int Quantity);

/// <summary>PUT /api/decks/{id}/cards/{deckCardId}/categories – names, primary first; missing ones are created; empty = type category.</summary>
public sealed record SetDeckCardCategoriesRequest(IReadOnlyList<string> Categories);

/// <summary>PUT /api/decks/{id}/cards/{deckCardId}/printing – another printing of the same name; Finish null keeps it when possible.</summary>
public sealed record ChangeDeckCardPrintingRequest(string CardUuid, string? Finish = null);

/// <summary>POST /api/decks/{id}/categories</summary>
public sealed record CreateDeckCategoryRequest(string Name, bool CountsInDeck = true);

/// <summary>PUT /api/decks/{id}/categories/{categoryId} – only the fields that are set change (name, then counts, then position).</summary>
public sealed record UpdateDeckCategoryRequest(string? Name = null, bool? CountsInDeck = null, int? Position = null);

/// <summary>POST /api/decks/{id}/import – a text decklist; Replace removes all cards and non-built-in categories first.</summary>
public sealed record ImportDeckTextRequest(string Text, bool Replace = false);

public sealed record DeckImportLineDto(int Line, string Text, string Problem);

public sealed record DeckImportResultDto(
    /// <summary>Cards added (sum of quantities).</summary>
    int Added,
    /// <summary>Lines that could not be read or matched (nothing added for them).</summary>
    IReadOnlyList<DeckImportLineDto> Unmatched,
    /// <summary>Lines added, but not quite as written.</summary>
    IReadOnlyList<DeckImportLineDto> Warnings,
    /// <summary>The deck after the import.</summary>
    DeckDetailDto Deck);

/// <summary>GET /api/decks/{id}/export</summary>
public sealed record DeckTextDto(string Text);
