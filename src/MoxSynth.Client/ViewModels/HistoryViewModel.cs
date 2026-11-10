using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;

namespace MoxSynth.Client.ViewModels;

/// <summary>Everything added to / removed from the collection, newest first.</summary>
public sealed partial class HistoryViewModel(MoxApiClient api, AppSession session, INavigator nav) : ViewModelBase
{
    private int _page;
    private int _totalPages;

    public ObservableCollection<HistoryItem> Events { get; } = [];

    private bool _subscribed;

    public override Task OnAppearingAsync()
    {
        if (!_subscribed) session.SignedOut += OnSignedOut;
        _subscribed = true;
        return LoadFirstPageAsync(showBusy: Events.Count == 0);
    }

    /// <summary>Back to the first-launch state so the next account never sees this one's history.</summary>
    private void OnSignedOut()
    {
        Events.Clear();
        _page = _totalPages = 0;
        Error = null;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadFirstPageAsync(showBusy: false);

    private Task LoadFirstPageAsync(bool showBusy) => RunAsync(async () =>
    {
        var page = await api.GetHistoryAsync(1);
        Events.Clear();
        foreach (var e in page.Items) Events.Add(new HistoryItem(e));
        _page = page.Page;
        _totalPages = page.TotalPages;
    }, showBusy);

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || _page == 0 || _page >= _totalPages) return;
        await RunAsync(async () =>
        {
            var page = await api.GetHistoryAsync(_page + 1);
            foreach (var e in page.Items) Events.Add(new HistoryItem(e));
            _page = page.Page;
        });
    }

    [RelayCommand]
    private Task OpenAsync(HistoryItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Event.CardUuid });
}
