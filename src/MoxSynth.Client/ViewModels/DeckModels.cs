using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>The deck Search adds cards to while its "Adding to …" banner is shown.</summary>
public sealed record DeckTarget(long Id, string Name);

/// <summary>A row of the deck list.</summary>
public sealed record DeckSummaryItem(DeckSummaryDto Deck, string FormatLabel)
{
    public string Name => Deck.Name;
    public string? CoverImageUrl => Deck.CoverImageUrl;
    public bool IsLegal => Deck.IsLegal;
    public bool IsNotLegal => !Deck.IsLegal;

    /// <summary>"Commander · 100 cards · €312.40" (no value without prices).</summary>
    public string Subtitle => $"{FormatLabel} · {Deck.CardCount} cards" +
                              (Deck.Value is { } v ? " · " + CardVocabulary.FormatPrice(Deck.Currency, v) : "");
}

/// <summary>A card row of the deck page; <see cref="Card"/> is replaced in place after each change.</summary>
public sealed partial class DeckCardItem(DeckCardDto card, string? currency) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(SetCode), nameof(ManaCost), nameof(ImageUrl), nameof(Quantity),
        nameof(PriceText), nameof(IsOwned), nameof(IsPartlyOwned), nameof(IsMissing), nameof(PartlyOwnedText),
        nameof(IllegalReason), nameof(IsIllegal))]
    private DeckCardDto _card = card;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceText))]
    private string? _currency = currency;

    public long DeckCardId => Card.DeckCardId;
    public string Name => Card.Name;
    public string SetCode => " " + Card.SetCode;
    public string? ManaCost => Card.ManaCost;
    public string? ImageUrl => Card.SmallImageUrl;
    public int Quantity => Card.Quantity;
    public string? PriceText => Card.Price is { } p ? CardVocabulary.FormatPrice(Currency, p) : null;

    public bool IsOwned => Card.OwnedExact >= Card.Quantity;
    public bool IsPartlyOwned => !IsOwned && Card.OwnedAny > 0 && Card.OwnedAny < Card.Quantity;
    public bool IsMissing => !IsOwned && Card.OwnedAny == 0;
    public string PartlyOwnedText => $"{Card.OwnedAny} of {Card.Quantity}";

    public string? IllegalReason => Card.IllegalReason;
    public bool IsIllegal => !string.IsNullOrEmpty(Card.IllegalReason);
}

/// <summary>Cards listed under one category (their primary one), for the grouped list.</summary>
public sealed class DeckCardGroup(DeckCategoryDto category) : ObservableCollection<DeckCardItem>
{
    public DeckCategoryDto Category { get; private set; } = category;

    public long CategoryId => Category.Id;
    public string Name => Category.Name;
    public bool CountsInDeck => Category.CountsInDeck;
    public bool IsNotCounting => !Category.CountsInDeck;
    public int CardCount { get; private set; }

    public void Update(DeckCategoryDto category, int cardCount)
    {
        Category = category;
        CardCount = cardCount;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Category)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Name)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountsInDeck)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsNotCounting)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(CardCount)));
    }
}

/// <summary>One bar of the mana curve (0 … 6+).</summary>
public sealed record ManaCurveBar(int Column, string Label, int Count, double BarHeight);

/// <summary>Mana curve, colours and types of the cards that count in the deck.</summary>
public sealed record DeckStats(IReadOnlyList<ManaCurveBar> Curve, string ColorsText, string TypesText)
{
    public const double MaxBarHeight = 80;

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    public static readonly DeckStats Empty = From([], []);

    public static DeckStats From(IReadOnlyList<DeckCardDto> cards, IReadOnlyList<DeckCategoryDto> categories)
    {
        var counting = categories.Where(c => c.CountsInDeck).Select(c => c.Id).ToHashSet();
        var counted = cards.Where(c => c.PrimaryCategoryId is { } id && counting.Contains(id)).ToList();
        var nonLands = counted.Where(c => !c.Types.Contains("Land", StringComparer.OrdinalIgnoreCase)).ToList();

        var buckets = new int[7];
        foreach (var c in nonLands) buckets[Math.Clamp((int)Math.Floor(c.ManaValue), 0, 6)] += c.Quantity;
        var max = Math.Max(1, buckets.Max());
        var curve = buckets.Select((n, i) => new ManaCurveBar(i, i == 6 ? "6+" : i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            n, n * MaxBarHeight / max)).ToList();

        var colors = new List<string>();
        foreach (var color in ColorOrder)
        {
            var n = nonLands.Where(c => c.Colors.Contains(color)).Sum(c => c.Quantity);
            if (n > 0) colors.Add($"{color} {n}");
        }
        var colorless = nonLands.Where(c => c.Colors.Count == 0).Sum(c => c.Quantity);
        if (colorless > 0) colors.Add($"C {colorless}");

        var types = counted
            .SelectMany(c => c.Types.Select(t => (Type: t, c.Quantity)))
            .GroupBy(x => x.Type)
            .Select(g => (g.Key, Count: g.Sum(x => x.Quantity)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key} {x.Count}");

        return new DeckStats(curve, colors.Count == 0 ? "–" : string.Join(" · ", colors), string.Join(" · ", types) is { Length: > 0 } t ? t : "–");
    }
}
