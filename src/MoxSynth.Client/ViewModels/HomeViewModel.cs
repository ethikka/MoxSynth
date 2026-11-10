using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>Dashboard: counts, value and the latest additions / removals.</summary>
public sealed partial class HomeViewModel(MoxApiClient api, AppSession session, INavigator nav) : ViewModelBase
{
    [ObservableProperty]
    private StatsDto? _stats;

    [ObservableProperty]
    private string _greeting = "";

    [ObservableProperty]
    private string _valueText = "–";

    [ObservableProperty]
    private string? _valueNote;

    public ObservableCollection<HistoryItem> Recent { get; } = [];

    private bool _subscribed;

    public override Task OnAppearingAsync()
    {
        if (!_subscribed) session.SignedOut += OnSignedOut;
        _subscribed = true;
        return LoadAsync(showBusy: Stats is null);
    }

    /// <summary>Back to the first-launch state so the next account never sees this one's data.</summary>
    private void OnSignedOut()
    {
        Stats = null;
        Greeting = "";
        ValueText = "–";
        ValueNote = null;
        Recent.Clear();
        Error = null;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(showBusy: false);

    private Task LoadAsync(bool showBusy) => RunAsync(async () =>
    {
        if (session.Me is null) await session.LoadAsync();
        Greeting = $"Hi {session.Me!.Profile.Name}";

        var statsTask = api.GetStatsAsync(session.Provider);
        var historyTask = api.GetHistoryAsync(1, 10);
        Stats = await statsTask;

        ValueText = Stats.Value is { } v ? CardVocabulary.FormatPrice(Stats.Currency, v) : "–";
        ValueNote = session.ProviderInfo is { } provider
            ? $"at {provider.Label}" + (Stats.UnpricedCopies > 0 ? $" · {Stats.UnpricedCopies} without price" : "")
            : null;

        var history = await historyTask;
        Recent.Clear();
        foreach (var e in history.Items) Recent.Add(new HistoryItem(e));
    }, showBusy);

    [RelayCommand]
    private Task OpenCardAsync(HistoryItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Event.CardUuid });

    [RelayCommand]
    private Task GoToAsync(string route) => nav.GoToAsync(route);
}
