using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class DecksPage : ViewModelPage
{
    public DecksPage(DecksViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private DecksViewModel Vm => (DecksViewModel)ViewModel;

    private void OnDeckTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<DeckSummaryItem>(sender!) is { } item) Vm.OpenCommand.Execute(item);
    }
}
