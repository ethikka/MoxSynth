using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>
/// One deck: summary, legality, stats and the cards by category, with quick edits (±1, move, remove). Building and
/// organising decks stays in the web app.
/// </summary>
public sealed partial class DeckViewModel(MoxApiClient api, AppSession session, INavigator nav, IDialogs dialogs,
    IClipboardService clipboard, DeckFormatCatalog formats) : ViewModelBase
{
    private long? _id;
    private IReadOnlyList<DeckFormatDto> _formats = [];

    // Answers can arrive out of order (a reload while a change is under way): only the newest request is shown.
    private int _requestSeq;
    private int _shownSeq;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(FormatText), nameof(CountText), nameof(ValueText), nameof(OwnedText),
        nameof(IsLegal), nameof(IsNotLegal), nameof(LegalityText), nameof(HasDeck))]
    private DeckDetailDto? _deck;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsToggleText))]
    private bool _showStats;

    [ObservableProperty]
    private DeckStats _stats = DeckStats.Empty;

    public ObservableCollection<DeckCardGroup> Groups { get; } = [];

    public bool HasDeck => Deck is not null;
    public string Title => Deck?.Name ?? "Deck";
    public string FormatText => Deck is null ? "" : DeckFormatCatalog.Label(_formats, Deck.Format);
    public string CountText => Deck is null ? "" : $"{Deck.CardCount} / {DeckFormatCatalog.DeckSize(_formats, Deck.Format)} cards";
    public string? ValueText => Deck?.Value is { } v ? CardVocabulary.FormatPrice(Deck.Currency, v) : null;
    public string OwnedText => Deck is null ? "" : $"{Deck.OwnedCount} of {Deck.CardCount} owned";

    public bool IsLegal => Deck?.Legality.IsLegal == true;
    public bool IsNotLegal => Deck is not null && !Deck.Legality.IsLegal;
    public string LegalityText => Deck is null ? "" : Deck.Legality.IsLegal
        ? "✓ Legal"
        : $"⚠ {Deck.Legality.Problems.Count} problem{(Deck.Legality.Problems.Count == 1 ? "" : "s")}";

    public string StatsToggleText => ShowStats ? "Hide stats" : "Show stats";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("id", out var value)) return;
        long? id = value switch
        {
            long l => l,
            int i => i,
            string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) => l,
            _ => null,
        };
        if (id is null || id == _id) return;
        _id = id;
        Deck = null;
        Groups.Clear();
        Stats = DeckStats.Empty;
    }

    /// <summary>Reloads each time it appears (cards may have been added from Search or the card page).</summary>
    public override Task OnAppearingAsync() => _id is null ? Task.CompletedTask : LoadAsync(showBusy: Deck is null);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(showBusy: false);

    public Task LoadAsync(bool showBusy = true)
    {
        var seq = ++_requestSeq;
        return RunAsync(async () =>
        {
            var formatList = await formats.GetAsync();
            var deck = await api.GetDeckAsync(_id!.Value, session.Provider);
            _formats = formatList;
            Show(deck, seq);
        }, showBusy);
    }

    /// <summary>Runs a change; the deck it returns replaces the shown one.</summary>
    private Task<bool> MutateAsync(Func<long, Task<DeckDetailDto>> change)
    {
        if (Deck is null) return Task.FromResult(false);
        var id = Deck.Id;
        var seq = ++_requestSeq;
        return RunAsync(async () => Show(await change(id), seq), showBusy: false);
    }

    private void Show(DeckDetailDto deck, int seq)
    {
        if (seq < _shownSeq) return;
        _shownSeq = seq;
        Deck = deck;
        Stats = DeckStats.From(deck.Cards, deck.Categories);
        SyncGroups(deck);
    }

    /// <summary>Updates the grouped list in place (keeps the scroll position) to match the deck.</summary>
    private void SyncGroups(DeckDetailDto deck)
    {
        var byCategory = deck.Cards.Where(c => c.PrimaryCategoryId is not null).ToLookup(c => c.PrimaryCategoryId!.Value);
        var wanted = deck.Categories
            .OrderBy(c => c.Position)
            .Select(c => (Category: c, Cards: byCategory[c.Id].ToList()))
            .Where(x => x.Cards.Count > 0)
            .ToList();

        var wantedIds = wanted.Select(x => x.Category.Id).ToHashSet();
        for (var i = Groups.Count - 1; i >= 0; i--)
            if (!wantedIds.Contains(Groups[i].CategoryId)) Groups.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var (category, cards) = wanted[i];
            var at = IndexOf(Groups, g => g.CategoryId == category.Id);
            DeckCardGroup group;
            if (at < 0)
            {
                group = new DeckCardGroup(category);
                Groups.Insert(i, group);
            }
            else
            {
                group = Groups[at];
                if (at != i) Groups.Move(at, i);
            }
            group.Update(category, cards.Sum(c => c.Quantity));
            SyncItems(group, cards, deck.Currency);
        }
    }

    private static void SyncItems(DeckCardGroup group, List<DeckCardDto> cards, string? currency)
    {
        var wantedIds = cards.Select(c => c.DeckCardId).ToHashSet();
        for (var i = group.Count - 1; i >= 0; i--)
            if (!wantedIds.Contains(group[i].DeckCardId)) group.RemoveAt(i);

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var at = IndexOf(group, x => x.DeckCardId == card.DeckCardId);
            if (at < 0)
            {
                group.Insert(i, new DeckCardItem(card, currency));
                continue;
            }
            if (at != i) group.Move(at, i);
            var item = group[i];
            if (item.Card != card) item.Card = card;
            item.Currency = currency;
        }
    }

    private static int IndexOf<T>(IList<T> list, Func<T, bool> match)
    {
        for (var i = 0; i < list.Count; i++)
            if (match(list[i])) return i;
        return -1;
    }

    [RelayCommand]
    private void ToggleStats() => ShowStats = !ShowStats;

    [RelayCommand]
    private async Task ShowProblemsAsync()
    {
        if (Deck is null || Deck.Legality.IsLegal) return;
        await dialogs.AlertAsync($"Not legal in {FormatText}", string.Join("\n", Deck.Legality.Problems.Select(p => "• " + p)));
    }

    [RelayCommand]
    private Task OpenCardAsync(DeckCardItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Card.CardUuid });

    [RelayCommand]
    private Task ShowIllegalReasonAsync(DeckCardItem item) =>
        item.IllegalReason is { Length: > 0 } reason ? dialogs.ToastAsync(reason) : Task.CompletedTask;

    [RelayCommand]
    private Task IncrementAsync(DeckCardItem item) =>
        MutateAsync(id => api.SetDeckCardQuantityAsync(id, item.DeckCardId, item.Quantity + 1, session.Provider));

    [RelayCommand]
    private async Task DecrementAsync(DeckCardItem item)
    {
        if (item.Quantity <= 1)
        {
            await RemoveAsync(item, confirm: true);
            return;
        }
        await MutateAsync(id => api.SetDeckCardQuantityAsync(id, item.DeckCardId, item.Quantity - 1, session.Provider));
    }

    private async Task RemoveAsync(DeckCardItem item, bool confirm)
    {
        if (confirm && !await dialogs.ConfirmAsync("Remove card", $"Remove {item.Name} from the deck?", "Remove")) return;
        await MutateAsync(id => api.SetDeckCardQuantityAsync(id, item.DeckCardId, 0, session.Provider));
    }

    private const string RemoveOption = "Remove from deck";

    /// <summary>"⋯": move the card to another category (as its primary one), or remove it.</summary>
    [RelayCommand]
    private async Task ShowCardMenuAsync(DeckCardItem item)
    {
        if (Deck is null) return;
        var categories = Deck.Categories.OrderBy(c => c.Position).ToList();
        var primary = item.Card.PrimaryCategoryId;
        var targets = categories.Where(c => c.Id != primary).ToList();
        var options = targets.Select(c => $"Move to {c.Name}…").Append(RemoveOption).ToList();

        var choice = await dialogs.ChooseAsync(item.Name, options);
        if (choice is null) return;
        if (choice == RemoveOption)
        {
            await RemoveAsync(item, confirm: false);
            return;
        }

        var target = targets[options.IndexOf(choice)];
        // New primary first; the other (secondary) categories stay, the old primary is left.
        var names = new List<string> { target.Name };
        foreach (var categoryId in item.Card.CategoryIds.Skip(1))
            if (categoryId != target.Id && categories.FirstOrDefault(c => c.Id == categoryId) is { } other)
                names.Add(other.Name);
        await MutateAsync(id => api.SetDeckCardCategoriesAsync(id, item.DeckCardId, names, session.Provider));
    }

    /// <summary>Search tab in "add to this deck" mode.</summary>
    [RelayCommand]
    private Task AddCardAsync() => Deck is null
        ? Task.CompletedTask
        : nav.GoToAsync(Routes.Search, new Dictionary<string, object> { ["deckId"] = Deck.Id, ["deckName"] = Deck.Name });

    [RelayCommand]
    private async Task CopyDecklistAsync()
    {
        if (Deck is null) return;
        var id = Deck.Id;
        string? text = null;
        if (!await RunAsync(async () => text = await api.ExportDeckTextAsync(id), showBusy: false)) return;
        await clipboard.SetTextAsync(text!);
        await dialogs.ToastAsync("Decklist copied");
    }
}
