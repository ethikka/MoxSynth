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

    public override Task OnAppearingAsync()
    {
        // Price provider changed in settings → refresh prices of the current results.
        if (_loadedProvider is not null && _loadedProvider != session.Provider) ScheduleSearch(immediately: true);
        return Task.CompletedTask;
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

    /// <summary>Photo → on-device OCR → server recognition. One match opens the card; several are listed.</summary>
    [RelayCommand]
    private async Task ScanAsync(string? source)
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var lines = await scanner.ScanAsync(fromGallery: source == "gallery");
            if (lines is null) return; // cancelled
            if (lines.Count == 0)
            {
                await dialogs.AlertAsync("No text found", "Couldn't read any text. Take the photo straight from above, with the card filling most of the picture.");
                return;
            }

            RecognizeResultDto? result = null;
            if (!await RunAsync(async () => result = await api.RecognizeAsync(lines, session.Provider))) return;

            if (result is not { Found: true })
            {
                await dialogs.AlertAsync("Not recognised",
                    $"Couldn't match a card name.\nRead as: \"{result?.TitleText}\"\nTry again with better light, or type the name.");
                return;
            }

            ShowCandidates(result);
            var printing = result.SetCode is not null && result.Number is not null ? $" ({result.SetCode} #{result.Number})" : "";
            if (result.Candidates.Count == 1)
                await OpenCardAsync(Results[0]);
            else
                await dialogs.ToastAsync($"Recognised {result.Name}{printing} – pick the printing");
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
}
