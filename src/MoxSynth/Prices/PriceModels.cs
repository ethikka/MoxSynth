using System.Globalization;

namespace MoxSynth.Prices;

public sealed class PriceOptions
{
    public const string SectionName = "Prices";

    /// <summary>MTGJSON paper retail providers to import (cardmarket, tcgplayer, cardkingdom, manapool).</summary>
    /// <remarks>No default items here: the configuration binder appends to an existing list.</remarks>
    public List<string> Providers { get; set; } = [];

    /// <summary>Configured providers without duplicates; Cardmarket + TCGplayer when nothing is configured.</summary>
    public IReadOnlyList<string> EffectiveProviders =>
        Providers.Count == 0 ? ["cardmarket", "tcgplayer"] : Providers.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToList();

    public string DefaultProvider { get; set; } = "cardmarket";

    public string DownloadUrl { get; set; } = "https://mtgjson.com/api/v5/AllPrices.json.zip";
}

public static class PriceProviders
{
    public static string Label(string provider) => provider switch
    {
        "cardmarket" => "Cardmarket",
        "tcgplayer" => "TCGplayer",
        "cardkingdom" => "Card Kingdom",
        "manapool" => "Mana Pool",
        _ => provider,
    };

    public static string CurrencySymbol(string? currency) => currency switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        _ => currency ?? "",
    };
}

/// <summary>Price of one printing in one finish at one provider, with its recent trend.</summary>
public sealed class PriceInfo
{
    public string Uuid { get; set; } = "";
    public string Provider { get; set; } = "";
    /// <summary>nonfoil / foil / etched (same names as the collection).</summary>
    public string Finish { get; set; } = "";
    public string Currency { get; set; } = "";
    public string FirstDate { get; set; } = "";
    public string LastDate { get; set; } = "";
    public double Price { get; set; }
    public double? Price7d { get; set; }
    public double? Price30d { get; set; }
    public double? PriceFirst { get; set; }
    /// <summary>Daily prices from <see cref="FirstDate"/> to <see cref="LastDate"/> (float32, NaN = no price that day).</summary>
    public byte[] History { get; set; } = [];

    /// <summary>The latest price is more than two weeks old (provider stopped listing this finish).</summary>
    public bool IsStale =>
        DateOnly.TryParseExact(LastDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)
        && last < DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-14);

    public double? Change7d => Change(Price7d);
    public double? Change30d => Change(Price30d);
    public double? ChangeFirst => Change(PriceFirst);

    private double? Change(double? then) => then is > 0 ? (Price - then.Value) / then.Value : null;

    public float[] HistoryValues()
    {
        var values = new float[History.Length / 4];
        Buffer.BlockCopy(History, 0, values, 0, values.Length * 4);
        return values;
    }

    /// <summary>Low / high (with dates) and average over the stored history (days without a price are skipped).</summary>
    public PriceStats Stats()
    {
        var values = HistoryValues();
        DateOnly.TryParseExact(FirstDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first);
        double low = double.MaxValue, high = double.MinValue, sum = 0;
        int lowIndex = 0, highIndex = 0, count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i])) continue;
            var v = Math.Round(values[i], 2); // stored as float32 – back to cents
            if (v < low) { low = v; lowIndex = i; }
            if (v >= high) { high = v; highIndex = i; } // latest date for ties
            sum += v;
            count++;
        }
        return count == 0
            ? new PriceStats(Price, LastDate, Price, LastDate, Price, 1)
            : new PriceStats(low, first.AddDays(lowIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                high, first.AddDays(highIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), sum / count, count);
    }

    public string Format(double? amount = null) =>
        PriceProviders.CurrencySymbol(Currency) + (amount ?? Price).ToString("N2", CultureInfo.InvariantCulture);
}

public sealed record PriceStats(double Low, string LowDate, double High, string HighDate, double Average, int DaysWithPrice);

/// <summary>Status of the price database / importer (shown on the Prices page).</summary>
public sealed record PriceStatus(
    bool Running,
    string Message,
    long CardsProcessed,
    DateTime? LastImportUtc,
    string? SourceDate,
    long Rows,
    string? Error);
