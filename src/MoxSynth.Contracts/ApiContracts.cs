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
    double? Value);

public sealed record CollectionPageDto(
    PageDto<CollectionGroupDto> Groups,
    long TotalCopies,
    double? TotalValue,
    string? Currency,
    long UnpricedCopies);

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
