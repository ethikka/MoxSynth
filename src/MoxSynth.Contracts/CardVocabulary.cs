namespace MoxSynth.Contracts;

/// <summary>An API code with its short and long display name (ToString = label, for pickers).</summary>
public sealed record VocabularyOption(string Value, string Short, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Display names for the string codes used in the API (finishes, conditions, sorting, removal reasons).</summary>
public static class CardVocabulary
{

    /// <summary>Cardmarket grading scale; Value = the API/database name.</summary>
    public static readonly IReadOnlyList<VocabularyOption> Conditions =
    [
        new VocabularyOption("Mint", "MT", "Mint"),
        new VocabularyOption("NearMint", "NM", "Near Mint"),
        new VocabularyOption("Excellent", "EX", "Excellent"),
        new VocabularyOption("Good", "GD", "Good"),
        new VocabularyOption("LightPlayed", "LP", "Light Played"),
        new VocabularyOption("Played", "PL", "Played"),
        new VocabularyOption("Poor", "PO", "Poor"),
    ];

    public static readonly IReadOnlyList<VocabularyOption> Finishes =
    [
        new VocabularyOption("nonfoil", "NF", "Non-foil"),
        new VocabularyOption("foil", "F", "Foil"),
        new VocabularyOption("etched", "E", "Etched"),
        new VocabularyOption("signed", "S", "Signed"),
    ];

    /// <summary>Collection sort orders (?sort=).</summary>
    public static readonly IReadOnlyList<VocabularyOption> CollectionSorts =
    [
        new VocabularyOption("Name", "Name", "Name"),
        new VocabularyOption("ValueDesc", "Value", "Value (highest first)"),
        new VocabularyOption("PriceDesc", "Price", "Price (highest first)"),
        new VocabularyOption("RisingDesc", "Risers", "Biggest risers (7 days)"),
        new VocabularyOption("FallingDesc", "Fallers", "Biggest fallers (7 days)"),
    ];

    public static readonly IReadOnlyList<string> RemovalReasons =
        ["Sold", "Traded", "Lost", "Damaged", "Gifted", "Entry error", "Other"];

    public static string ConditionShort(string value) => Conditions.FirstOrDefault(c => c.Value == value)?.Short ?? value;
    public static string ConditionLabel(string value) => Conditions.FirstOrDefault(c => c.Value == value)?.Label ?? value;
    public static string FinishLabel(string value) => Finishes.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    public static string CurrencySymbol(string? currency) => currency switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        null or "" => "",
        _ => currency + " ",
    };

    public static string FormatPrice(string? currency, double amount) =>
        CurrencySymbol(currency) + amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
}
