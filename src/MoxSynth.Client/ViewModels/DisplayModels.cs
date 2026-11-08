using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

public enum Trend { Flat, Up, Down }

/// <summary>A price ready for display: "€1.49", "▲8%", sparkline values.</summary>
public sealed record PriceView(
    string Text,
    string? ChangeText,
    Trend Trend,
    bool IsStale,
    IReadOnlyList<double?> History,
    PriceDto Source)
{
    public static PriceView? From(PriceDto? p, double? amount = null)
    {
        if (p is null) return null;
        var trend = p.Change7d is null || Math.Abs(p.Change7d.Value) < 0.005 ? Trend.Flat : p.Change7d > 0 ? Trend.Up : Trend.Down;
        return new PriceView(CardVocabulary.FormatPrice(p.Currency, amount ?? p.Price), Format.Change(p.Change7d), trend,
            p.IsStale, p.History ?? [], p);
    }
}

public static class Format
{
    public static string? Change(double? change) => change is null
        ? null
        : (Math.Abs(change.Value) < 0.005 ? "→" : change > 0 ? "▲" : "▼") +
          Math.Abs(change.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string SignedPercent(double? change) => change is null
        ? "–"
        : (change > 0 ? "+" : "") + (change.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static Trend TrendOf(double? change) =>
        change is null || Math.Abs(change.Value) < 0.005 ? Trend.Flat : change > 0 ? Trend.Up : Trend.Down;

    public static string LocalDateTime(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Rarity(string? rarity) => string.IsNullOrEmpty(rarity) ? "" : char.ToUpperInvariant(rarity[0]) + rarity[1..];
}

/// <summary>A search result row.</summary>
public sealed partial class CardItem(CardSummaryDto card) : ObservableObject
{
    public CardSummaryDto Card { get; } = card;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OwnedText), nameof(IsOwned))]
    private long _owned = card.Owned;

    public string Name => Card.Name;
    public string? ManaCost => Card.ManaCost;
    public string? ImageUrl => Card.SmallImageUrl;
    public string Subtitle => $"{Card.SetCode} · #{Card.Number} · {Format.Rarity(Card.Rarity)}";
    public string? SetName => Card.SetName;
    public string? Type => Card.Type;

    public bool IsOwned => Owned > 0;
    public string OwnedText => Owned > 0 ? $"{Owned}× owned" : "";

    /// <summary>Price of the main (first) finish.</summary>
    public PriceView? MainPrice { get; } = PriceView.From(card.Prices.FirstOrDefault());

    /// <summary>"F €3.20 · E €5.00" – the other finishes.</summary>
    public string OtherPricesText { get; } = string.Join(" · ", card.Prices.Skip(1).Select(p =>
        $"{CardVocabulary.Finishes.FirstOrDefault(f => f.Value == p.Finish)?.Short ?? p.Finish} {CardVocabulary.FormatPrice(p.Currency, p.Price)}"));

    public string FinishesText => string.Join(", ", Card.Finishes.Select(CardVocabulary.FinishLabel));
}

/// <summary>A collection row (copies of one printing / finish / condition / language).</summary>
public sealed partial class GroupItem(CollectionGroupDto group) : ObservableObject
{
    public CollectionGroupDto Group { get; } = group;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private long _quantity = group.Quantity;

    public string Name => Group.Name;
    public string? ManaCost => Group.ManaCost;
    public string? ImageUrl => Group.SmallImageUrl;
    public string Subtitle =>
        $"{Group.SetCode} #{Group.Number} · {CardVocabulary.FinishLabel(Group.Finish)} · {CardVocabulary.ConditionShort(Group.Condition)}" +
        (Group.Language == "English" ? "" : " · " + Group.Language);

    public PriceView? Price { get; } = PriceView.From(group.Price);

    public string? ValueText => Group.Price is { } p && Quantity > 1 ? "= " + CardVocabulary.FormatPrice(p.Currency, p.Price * Quantity) : null;
}

public sealed record HistoryItem(HistoryEventDto Event)
{
    public bool IsAdded => Event.Kind == "Added";
    public string Symbol => IsAdded ? "+" : "−";
    public string Title => Event.Name;
    public string Subtitle =>
        $"{Event.SetCode} #{Event.Number} · {CardVocabulary.FinishLabel(Event.Finish)} · {CardVocabulary.ConditionShort(Event.Condition)}" +
        (Event.RemovalReason is { Length: > 0 } r ? $" · {r}" : "");
    public string When => Format.LocalDateTime(Event.At);
}

/// <summary>One owned (or removed) copy on the card page.</summary>
public sealed record CopyItem(CopyDto Copy)
{
    public bool IsRemoved => Copy.RemovedAt is not null;
    public string Title =>
        $"{CardVocabulary.FinishLabel(Copy.Finish)} · {CardVocabulary.ConditionLabel(Copy.Condition)}" +
        (Copy.Language == "English" ? "" : " · " + Copy.Language);
    public string Subtitle => IsRemoved
        ? $"Removed {Format.LocalDateTime(Copy.RemovedAt!.Value)}{(Copy.RemovalReason is { Length: > 0 } r ? " · " + r : "")}"
        : $"Added {Format.LocalDateTime(Copy.AddedAt)}" +
          (Copy.PurchasePrice is { } pp ? $" · paid {pp.ToString("0.00", CultureInfo.InvariantCulture)}" : "");
    public string? Notes => Copy.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Copy.Notes);
}

/// <summary>A row of the price details table on the card page.</summary>
public sealed record PriceRow(PriceDto Price, string ProviderLabel, bool IsSelectedProvider)
{
    public string Title => $"{ProviderLabel} · {CardVocabulary.FinishLabel(Price.Finish)}";
    public string Current => CardVocabulary.FormatPrice(Price.Currency, Price.Price);
    public string Change7d => Format.SignedPercent(Price.Change7d);
    public Trend Trend7d => Format.TrendOf(Price.Change7d);
    public string Change30d => Format.SignedPercent(Price.Change30d);
    public Trend Trend30d => Format.TrendOf(Price.Change30d);
    public string ChangeFirst => Format.SignedPercent(Price.ChangeFirst);
    public Trend TrendFirst => Format.TrendOf(Price.ChangeFirst);
    public string Low => Price.Stats is { } s ? CardVocabulary.FormatPrice(Price.Currency, s.Low) : "–";
    public string High => Price.Stats is { } s ? CardVocabulary.FormatPrice(Price.Currency, s.High) : "–";
    public string Average => Price.Stats is { } s ? CardVocabulary.FormatPrice(Price.Currency, s.Average) : "–";
    public string Period => $"since {Price.FirstDate}";
    public string LastUpdate => Price.IsStale ? $"{Price.LastDate} (old)" : Price.LastDate;
    public IReadOnlyList<double?> History => Price.History ?? [];
}
