using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>
/// Card search as you type (same rules as the web app: words = parts of the name, a number = collector number,
/// plus an optional Scryfall-syntax box), quick add, and scanning a card with the camera.
/// </summary>
public sealed partial class SearchViewModel(MoxApiClient api, AppSession session, INavigator nav, IDialogs dialogs,
    ICardScanner scanner) : ViewModelBase
{
    private const int PageSize = 30;
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _searchCts;
    private int _page;
    private int _totalPages;
    private bool _suppressSearch;
    private string? _loadedProvider;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _scryfall = "";

    [ObservableProperty]
    private bool _showScryfall;

    [ObservableProperty]
    private string _resultText = "Type a card name, a collector number, or scan a card.";

    [ObservableProperty]
    private bool _isScanning;

    public ObservableCollection<CardItem> Results { get; } = [];

    /// <summary>The last search task (lets tests await the debounced search).</summary>
    public Task? PendingSearch { get; private set; }

    partial void OnQueryChanged(string value) => ScheduleSearch();
    partial void OnScryfallChanged(string value) => ScheduleSearch();

    private bool _subscribed;

    public override Task OnAppearingAsync()
    {
        if (!_subscribed) session.SignedOut += OnSignedOut;
        _subscribed = true;
        // Price provider changed in settings → refresh prices of the current results.
        if (_loadedProvider is not null && _loadedProvider != session.Provider) ScheduleSearch(immediately: true);
        return Task.CompletedTask;
    }

    /// <summary>Back to the first-launch state so the next account never sees this one's results.</summary>
    private void OnSignedOut()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        PendingSearch = null;
        _suppressSearch = true;
        Query = "";
        Scryfall = "";
        _suppressSearch = false;
        ShowScryfall = false;
        Results.Clear();
        _page = _totalPages = 0;
        _loadedProvider = null;
        ResultText = "Type a card name, a collector number, or scan a card.";
        Error = null;
        TargetDeck = null;
    }

    private void ScheduleSearch(bool immediately = false)
    {
        if (_suppressSearch) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        PendingSearch = SearchAsync(immediately ? TimeSpan.Zero : Debounce, cts.Token);
    }

    private async Task SearchAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Query) && string.IsNullOrWhiteSpace(Scryfall))
        {
            Results.Clear();
            _page = _totalPages = 0;
            ResultText = "Type a card name, a collector number, or scan a card.";
            return;
        }

        await RunAsync(async () =>
        {
            var page = await api.SearchCardsAsync(Query, Scryfall, session.Provider, 1, PageSize, ct);
            if (ct.IsCancellationRequested) return;
            Results.Clear();
            foreach (var card in page.Items) Results.Add(new CardItem(card));
            _page = page.Page;
            _totalPages = page.TotalPages;
            _loadedProvider = session.Provider;
            ResultText = page.TotalCount == 0 ? "No cards found." : $"{page.TotalCount:N0} printings";
        });
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || _page == 0 || _page >= _totalPages) return;
        var ct = _searchCts?.Token ?? default;
        await RunAsync(async () =>
        {
            var page = await api.SearchCardsAsync(Query, Scryfall, session.Provider, _page + 1, PageSize, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var card in page.Items) Results.Add(new CardItem(card));
            _page = page.Page;
        });
    }

    [RelayCommand]
    private void Clear()
    {
        _suppressSearch = true;
        Query = "";
        Scryfall = "";
        _suppressSearch = false;
        ScheduleSearch(immediately: true);
    }

    [RelayCommand]
    private Task OpenCardAsync(CardItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Card.Uuid });

    /// <summary>Adds one Near Mint copy; asks for the finish when the printing has several.</summary>
    [RelayCommand]
    private async Task QuickAddAsync(CardItem item)
    {
        if (TargetDeck is not null)
        {
            await AddToTargetDeckAsync(item);
            return;
        }

        var finish = item.Card.Finishes.FirstOrDefault() ?? "nonfoil";
        if (item.Card.Finishes.Count > 1)
        {
            var labels = item.Card.Finishes.Select(CardVocabulary.FinishLabel).ToList();
            var chosen = await dialogs.ChooseAsync($"Add {item.Name}", labels);
            if (chosen is null) return;
            finish = item.Card.Finishes[labels.IndexOf(chosen)];
        }

        await RunAsync(async () =>
        {
            var result = await api.AddCardsAsync(new AddCardsDto(item.Card.Uuid, finish));
            item.Owned = result.Owned;
            await dialogs.ToastAsync($"Added {item.Name} ({item.Card.SetCode} #{item.Card.Number}, {CardVocabulary.FinishLabel(finish)})");
        }, showBusy: false);
    }

    /// <summary>
    /// Camera: live view that reads frames until the server recognises a card. Gallery: photo → on-device OCR →
    /// server recognition. One match opens the card; several are listed.
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync(string? source)
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            if (source != "gallery")
            {
                var live = await scanner.ScanLiveAsync((lines, ct) => api.RecognizeAsync(lines, session.Provider, ct));
                switch (live.Outcome)
                {
                    case LiveScanOutcome.Cancelled:
                        return;
                    case LiveScanOutcome.Found:
                        await ShowRecognizedAsync(live.Result!);
                        return;
                }
                // The user picked "Gallery" in the live view: continue with the photo path below.
            }

            var photoLines = await scanner.ScanAsync(fromGallery: true);
            if (photoLines is null) return; // cancelled
            if (photoLines.Count == 0)
            {
                await dialogs.AlertAsync("No text found", "Couldn't read any text. Take the photo straight from above, with the card filling most of the picture.");
                return;
            }

            RecognizeResultDto? result = null;
            if (!await RunAsync(async () => result = await api.RecognizeAsync(photoLines, session.Provider))) return;

            if (result is not { Found: true })
            {
                await dialogs.AlertAsync("Not recognised",
                    $"Couldn't match a card name.\nRead as: \"{result?.TitleText}\"\nTry again with better light, or type the name.");
                return;
            }

            await ShowRecognizedAsync(result);
        }
        catch (Exception ex) when (ex is not ApiException)
        {
            await dialogs.AlertAsync("Scan failed", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// A recognised card: lists its printings; a single printing opens (or, when owned, asks what to do; while adding to a
    /// deck, goes into the deck).
    /// </summary>
    private async Task ShowRecognizedAsync(RecognizeResultDto result)
    {
        ShowCandidates(result);
        var printing = result.SetCode is not null && result.Number is not null ? $" ({result.SetCode} #{result.Number})" : "";
        if (result.Candidates.Count == 1)
        {
            if (TargetDeck is not null) await AddToTargetDeckAsync(Results[0]);
            else if (Results[0].IsOwned) await HandleOwnedScanAsync(Results[0]);
            else await OpenCardAsync(Results[0]);
        }
        else
            await dialogs.ToastAsync($"Recognised {result.Name}{printing} – pick the printing");
    }

    private const string AddCopyOption = "Add a new copy";
    private const string RemoveCopyOption = "Remove an existing copy";
    private const string OpenCardOption = "Just show the card";

    /// <summary>The scanned printing is already in the collection: add another copy, remove one, or just open it.</summary>
    private async Task HandleOwnedScanAsync(CardItem item)
    {
        var choice = await dialogs.ChooseAsync($"You already own {item.Owned}× {item.Name} ({item.Card.SetCode} #{item.Card.Number})",
            [AddCopyOption, RemoveCopyOption, OpenCardOption]);
        switch (choice)
        {
            case AddCopyOption:
                await QuickAddAsync(item);
                break;
            case RemoveCopyOption:
                await RemoveScannedCopyAsync(item);
                break;
            case OpenCardOption:
                await OpenCardAsync(item);
                break;
        }
    }

    /// <summary>Lets the user pick which owned copy of the printing leaves the collection, and why.</summary>
    private async Task RemoveScannedCopyAsync(CardItem item)
    {
        CardDetailDto? card = null;
        if (!await RunAsync(async () => card = await api.GetCardAsync(item.Card.Uuid, session.Provider))) return;

        var owned = card!.Copies.Where(c => c.RemovedAt is null).ToList();
        if (owned.Count == 0)
        {
            item.Owned = 0;
            await dialogs.AlertAsync("Nothing to remove", $"There is no copy of {item.Name} left in your collection.");
            return;
        }

        var copy = owned[0];
        if (owned.Count > 1)
        {
            // Numbered so identical copies still get distinct options.
            var labels = owned.Select((c, i) => $"{i + 1}. {DescribeCopy(c)}").ToList();
            var chosen = await dialogs.ChooseAsync($"Which copy of {item.Name}?", labels);
            if (chosen is null) return;
            copy = owned[labels.IndexOf(chosen)];
        }

        var reason = await dialogs.ChooseAsync("Why is this copy leaving?", CardVocabulary.RemovalReasons);
        if (reason is null) return;

        await RunAsync(async () =>
        {
            await api.RemoveCopyAsync(copy.Id, reason);
            item.Owned = owned.Count - 1;
            await dialogs.ToastAsync($"Removed {item.Name} ({CardVocabulary.FinishLabel(copy.Finish)}, {CardVocabulary.ConditionShort(copy.Condition)}) – {reason}");
        }, showBusy: false);
    }

    private static string DescribeCopy(CopyDto c)
    {
        var parts = new List<string>
        {
            CardVocabulary.FinishLabel(c.Finish),
            CardVocabulary.ConditionShort(c.Condition),
            c.Language,
            $"added {c.AddedAt.ToLocalTime():d MMM yyyy}",
        };
        if (c.PurchasePrice is { } price) parts.Add(price.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture));
        return string.Join(" · ", parts);
    }

    private void ShowCandidates(RecognizeResultDto result)
    {
        _searchCts?.Cancel();
        _suppressSearch = true;
        Query = result.Number is null ? "" : result.Number;
        Scryfall = $"!\"{result.Name}\"" + (result.SetCode is null ? "" : $" e:{result.SetCode.ToLowerInvariant()}");
        _suppressSearch = false;

        Results.Clear();
        foreach (var card in result.Candidates) Results.Add(new CardItem(card));
        ShowScryfall = true;
        _page = 1;
        _totalPages = 1;
        _loadedProvider = session.Provider;
        ResultText = $"Scanned: {result.Name} – {result.Candidates.Count} printing{(result.Candidates.Count == 1 ? "" : "s")}";
    }

    // ------------------------------------------------------------ "Adding to deck" mode
    // Entered from the deck page's "Add card" (parameters deckId, deckName): "+" and a single-result scan add the card
    // to that deck instead of the collection. The banner's ✕ or signing out ends it.

    /// <summary>The deck "+" and scans add to; null = normal mode (add to the collection).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddingToDeck), nameof(AddingToDeckText))]
    private DeckTarget? _targetDeck;

    public bool IsAddingToDeck => TargetDeck is not null;
    public string AddingToDeckText => TargetDeck is { } deck ? $"Adding to {deck.Name}" : "";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("deckId", out var value) || value is not long id) return;
        var name = parameters.TryGetValue("deckName", out var n) && n is string s && s.Length > 0 ? s : "deck";
        TargetDeck = new DeckTarget(id, name);
    }

    [RelayCommand]
    private void StopAddingToDeck() => TargetDeck = null;

    /// <summary>Adds one copy of the printing (its first finish, non-foil when it has one) to the target deck.</summary>
    private async Task AddToTargetDeckAsync(CardItem item)
    {
        if (TargetDeck is not { } deck) return;
        var finishes = item.Card.Finishes;
        var finish = finishes.Contains("nonfoil") ? "nonfoil" : finishes.FirstOrDefault() ?? "nonfoil";
        await RunAsync(async () =>
        {
            await api.AddDeckCardAsync(deck.Id, new AddDeckCardRequest(item.Card.Uuid, 1, finish), session.Provider);
            await dialogs.ToastAsync($"Added {item.Name} to {deck.Name}");
        }, showBusy: false);
    }
}
