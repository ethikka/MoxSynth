using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;

namespace MoxSynth.Client.ViewModels;

/// <summary>Your decks, most recently updated first (building them happens in the web app).</summary>
public sealed partial class DecksViewModel(MoxApiClient api, AppSession session, INavigator nav, DeckFormatCatalog formats)
    : ViewModelBase
{
    private CancellationTokenSource? _loadCts;
    private bool _loaded;
    private string? _loadedProvider;
    private bool _subscribed;

    public ObservableCollection<DeckSummaryItem> Decks { get; } = [];

    /// <summary>The last load task (lets tests await it).</summary>
    public Task? PendingLoad { get; private set; }

    /// <summary>Reloads when shown (cards may have been added elsewhere) – keeps the list while loading.</summary>
    public override Task OnAppearingAsync()
    {
        if (!_subscribed) session.SignedOut += OnSignedOut;
        _subscribed = true;
        Schedule(showBusy: !_loaded || _loadedProvider != session.Provider);
        return PendingLoad!;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Schedule(showBusy: false);
        return PendingLoad!;
    }

    /// <summary>Back to the first-launch state so the next account never sees this one's decks.</summary>
    private void OnSignedOut()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        PendingLoad = null;
        Decks.Clear();
        _loaded = false;
        _loadedProvider = null;
        Error = null;
    }

    private void Schedule(bool showBusy)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        PendingLoad = LoadAsync(showBusy, cts.Token);
    }

    private Task LoadAsync(bool showBusy, CancellationToken ct) => RunAsync(async () =>
    {
        var provider = session.Provider;
        var formatList = await formats.GetAsync();
        var decks = await api.GetDecksAsync(provider, ct);
        if (ct.IsCancellationRequested) return;
        Decks.Clear();
        foreach (var d in decks.OrderByDescending(d => d.UpdatedAt))
            Decks.Add(new DeckSummaryItem(d, DeckFormatCatalog.Label(formatList, d.Format)));
        _loaded = true;
        _loadedProvider = provider;
    }, showBusy);

    [RelayCommand]
    private Task OpenAsync(DeckSummaryItem item) =>
        nav.GoToAsync(Routes.Deck, new Dictionary<string, object> { ["id"] = item.Deck.Id });
}
