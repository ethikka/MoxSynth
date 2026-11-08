using MoxSynth.Client.Services;
using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class HomePage : ViewModelPage
{
    private readonly SearchViewModel _search;

    public HomePage(HomeViewModel viewModel, SearchViewModel search) : base(viewModel)
    {
        _search = search;
        InitializeComponent();
    }

    private HomeViewModel Vm => (HomeViewModel)ViewModel;

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        // Scanning lives on the search page (results / printings are listed there).
        await Shell.Current.GoToAsync(Routes.Search);
        await _search.ScanCommand.ExecuteAsync("camera");
    }

    private void OnRecentTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<HistoryItem>(sender!) is { } item) Vm.OpenCardCommand.Execute(item);
    }

    private async void OnSettingsClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync(Routes.Settings);
}
