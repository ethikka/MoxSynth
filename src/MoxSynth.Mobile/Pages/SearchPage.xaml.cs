using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class SearchPage : ViewModelPage
{
    public SearchPage(SearchViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private SearchViewModel Vm => (SearchViewModel)ViewModel;

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<CardItem>(sender!) is { } item) Vm.OpenCardCommand.Execute(item);
    }

    private void OnQuickAddClicked(object? sender, EventArgs e)
    {
        if (ItemOf<CardItem>(sender!) is { } item) Vm.QuickAddCommand.Execute(item);
    }

    private void OnToggleScryfall(object? sender, EventArgs e) => Vm.ShowScryfall = !Vm.ShowScryfall;
}
