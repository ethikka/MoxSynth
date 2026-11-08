using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>Your collection grouped like the web page: filter as you type, Scryfall syntax, sort by value, ±1.</summary>
public sealed partial class CollectionViewModel(MoxApiClient api, AppSession session, INavigator nav, IDialogs dialogs)
    : ViewModelBase
{
    private const int PageSize = 50;

    private CancellationTokenSource? _searchCts;
    private int _page;
    private int _totalPages;
    private bool _loaded;
    private string? _loadedProvider;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _scryfall = "";

    [ObservableProperty]
    private bool _showScryfall;

    [ObservableProperty]
    private VocabularyOption _selectedSort = CardVocabulary.CollectionSorts[0];

    [ObservableProperty]
    private string _summary = "";

    public ObservableCollection<GroupItem> Groups { get; } = [];
    public IReadOnlyList<VocabularyOption> Sorts => CardVocabulary.CollectionSorts;

    /// <summary>The last load task (lets tests await the debounced reload).</summary>
    public Task? PendingLoad { get; private set; }

    partial void OnQueryChanged(string value) => Schedule(SearchViewModel.Debounce);
    partial void OnScryfallChanged(string value) => Schedule(SearchViewModel.Debounce);
    partial void OnSelectedSortChanged(VocabularyOption value) => Schedule(TimeSpan.Zero);

    /// <summary>Reloads when shown (cards may have been added elsewhere) – keeps the list while loading.</summary>
    public override Task OnAppearingAsync()
    {
        Schedule(TimeSpan.Zero, showBusy: !_loaded || _loadedProvider != session.Provider);
        return PendingLoad!;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Schedule(TimeSpan.Zero, showBusy: false);
        return PendingLoad!;
    }

    private void Schedule(TimeSpan delay, bool showBusy = true)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        PendingLoad = LoadAsync(delay, showBusy, cts.Token);
    }

    private async Task LoadAsync(TimeSpan delay, bool showBusy, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var page = await api.GetCollectionAsync(Query, Scryfall, SelectedSort.Value, session.Provider, 1, PageSize, ct);
            if (ct.IsCancellationRequested) return;
            Groups.Clear();
            foreach (var g in page.Groups.Items) Groups.Add(new GroupItem(g));
            _page = page.Groups.Page;
            _totalPages = page.Groups.TotalPages;
            _loaded = true;
            _loadedProvider = session.Provider;
            Summary = SummaryOf(page);
        }, showBusy);
    }

    private static string SummaryOf(CollectionPageDto page)
    {
        var text = $"{page.TotalCopies:N0} cards in {page.Groups.TotalCount:N0} groups";
        if (page.TotalValue is { } value) text += " · " + CardVocabulary.FormatPrice(page.Currency, value);
        return text;
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || _page == 0 || _page >= _totalPages) return;
        var ct = _searchCts?.Token ?? default;
        await RunAsync(async () =>
        {
            var page = await api.GetCollectionAsync(Query, Scryfall, SelectedSort.Value, session.Provider, _page + 1, PageSize, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var g in page.Groups.Items) Groups.Add(new GroupItem(g));
            _page = page.Groups.Page;
        });
    }

    [RelayCommand]
    private Task OpenAsync(GroupItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Group.CardUuid });

    [RelayCommand]
    private async Task IncrementAsync(GroupItem item)
    {
        var g = item.Group;
        await RunAsync(async () =>
        {
            await api.AddCardsAsync(new AddCardsDto(g.CardUuid, g.Finish, g.Condition, g.Language));
            item.Quantity++;
        }, showBusy: false);
    }

    [RelayCommand]
    private async Task DecrementAsync(GroupItem item)
    {
        var g = item.Group;
        if (item.Quantity == 1 &&
            !await dialogs.ConfirmAsync("Remove last copy", $"Remove your last {g.Name} ({g.SetCode} #{g.Number})?", "Remove"))
            return;

        await RunAsync(async () =>
        {
            await api.RemoveOneAsync(new RemoveOneDto(g.CardUuid, g.Finish, g.Condition, g.Language));
            item.Quantity--;
            if (item.Quantity == 0) Groups.Remove(item);
        }, showBusy: false);
    }
}
