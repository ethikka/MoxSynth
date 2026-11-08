using MoxSynth.Infrastructure;

namespace MoxSynth.Collection;

/// <summary>Cardmarket-style grading scale.</summary>
public enum CardCondition
{
    Mint,
    NearMint,
    Excellent,
    Good,
    LightPlayed,
    Played,
    Poor,
}

public static class CardConditionExtensions
{
    public static string Short(this CardCondition c) => c switch
    {
        CardCondition.Mint => "MT",
        CardCondition.NearMint => "NM",
        CardCondition.Excellent => "EX",
        CardCondition.Good => "GD",
        CardCondition.LightPlayed => "LP",
        CardCondition.Played => "PL",
        CardCondition.Poor => "PO",
        _ => c.ToString(),
    };

    public static string Label(this CardCondition c) => c switch
    {
        CardCondition.NearMint => "Near Mint",
        CardCondition.LightPlayed => "Light Played",
        _ => c.ToString(),
    };
}

public static class RemovalReasons
{
    public static readonly IReadOnlyList<string> All = ["Sold", "Traded", "Lost", "Damaged", "Gifted", "Entry error", "Other"];
}

/// <summary>
/// One physical card in the collection. A card is never deleted: removing it sets <see cref="RemovedAt"/>,
/// which keeps a complete history and allows "what did I own on date X" queries.
/// </summary>
public sealed class CollectionCard
{
    private DateTime _addedAt;
    private DateTime? _removedAt;

    public long Id { get; set; }

    /// <summary>MTGJSON uuid of the printing (cards.uuid in AllPrintings).</summary>
    public string CardUuid { get; set; } = "";

    // Snapshot of catalogue data at time of adding, so the collection stays readable without the card db.
    public string Name { get; set; } = "";
    public string SetCode { get; set; } = "";
    public string Number { get; set; } = "";

    public string Finish { get; set; } = "nonfoil";
    public CardCondition Condition { get; set; } = CardCondition.NearMint;
    public string Language { get; set; } = "English";
    public double? PurchasePrice { get; set; }
    public string? Notes { get; set; }

    /// <summary>UTC moment the card entered the collection.</summary>
    public DateTime AddedAt
    {
        get => _addedAt;
        set => _addedAt = SqliteTime.AsUtc(value);
    }

    /// <summary>UTC moment the card left the collection; null while still owned.</summary>
    public DateTime? RemovedAt
    {
        get => _removedAt;
        set => _removedAt = SqliteTime.AsUtc(value);
    }

    public string? RemovalReason { get; set; }

    public bool IsOwned => RemovedAt is null;
}

public sealed class AddCardsRequest
{
    public required string CardUuid { get; init; }
    public required string Name { get; init; }
    public required string SetCode { get; init; }
    public required string Number { get; init; }
    public string Finish { get; init; } = "nonfoil";
    public CardCondition Condition { get; init; } = CardCondition.NearMint;
    public string Language { get; init; } = "English";
    public double? PurchasePrice { get; init; }
    public string? Notes { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Defaults to now; can be set to back-date an acquisition.</summary>
    public DateTime? AddedAt { get; init; }
}

/// <summary>Owned copies grouped by printing + finish + condition + language.</summary>
public sealed class CollectionGroup
{
    private DateTime _firstAddedAt;
    private DateTime _lastAddedAt;

    public string CardUuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string SetCode { get; set; } = "";
    public string Number { get; set; } = "";
    public string Finish { get; set; } = "";
    public CardCondition Condition { get; set; }
    public string Language { get; set; } = "";
    public long Quantity { get; set; }

    public DateTime FirstAddedAt { get => _firstAddedAt; set => _firstAddedAt = SqliteTime.AsUtc(value); }
    public DateTime LastAddedAt { get => _lastAddedAt; set => _lastAddedAt = SqliteTime.AsUtc(value); }
}

public sealed class CollectionQuery
{
    public string? Name { get; set; }
    public string? SetCode { get; set; }
    public string? Finish { get; set; }
    /// <summary>Optional Scryfall-syntax filter (same syntax as on the Search page).</summary>
    public string? Scryfall { get; set; }
    /// <summary>When set, shows the collection as it was at this UTC moment.</summary>
    public DateTime? AsOf { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record CollectionPage(PagedResult<CollectionGroup> Groups, long TotalCopies);

/// <summary>A file import into the collection (see <see cref="ArchidektImporter"/>).</summary>
public sealed class ImportRecord
{
    private DateTime _importedAt;
    private DateTime? _undoneAt;

    public long Id { get; set; }
    public string Source { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileHash { get; set; } = "";
    public long CardCount { get; set; }
    public DateTime ImportedAt { get => _importedAt; set => _importedAt = SqliteTime.AsUtc(value); }
    public DateTime? UndoneAt { get => _undoneAt; set => _undoneAt = SqliteTime.AsUtc(value); }
}

public sealed class CollectionStats
{
    public long OwnedCopies { get; set; }
    public long UniquePrintings { get; set; }
    public long UniqueNames { get; set; }
    public long RemovedCopies { get; set; }
    public long AddedLast30Days { get; set; }
    public long RemovedLast30Days { get; set; }
}

public sealed class HistoryEvent
{
    private DateTime _at;

    public long Id { get; set; }
    public string CardUuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string SetCode { get; set; } = "";
    public string Number { get; set; } = "";
    public string Finish { get; set; } = "";
    public CardCondition Condition { get; set; }
    /// <summary>"Added" or "Removed".</summary>
    public string Kind { get; set; } = "";
    public string? RemovalReason { get; set; }
    public DateTime At { get => _at; set => _at = SqliteTime.AsUtc(value); }
}
