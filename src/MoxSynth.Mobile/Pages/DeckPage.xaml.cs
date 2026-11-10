using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class DeckPage : ViewModelPage
{
    public DeckPage(DeckViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private DeckViewModel Vm => (DeckViewModel)ViewModel;

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<DeckCardItem>(sender!) is { } item) Vm.OpenCardCommand.Execute(item);
    }

    private void OnIllegalTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<DeckCardItem>(sender!) is { } item) Vm.ShowIllegalReasonCommand.Execute(item);
    }

    private void OnIncrementClicked(object? sender, EventArgs e)
    {
        if (ItemOf<DeckCardItem>(sender!) is { } item) Vm.IncrementCommand.Execute(item);
    }

    private void OnDecrementClicked(object? sender, EventArgs e)
    {
        if (ItemOf<DeckCardItem>(sender!) is { } item) Vm.DecrementCommand.Execute(item);
    }

    private void OnMenuClicked(object? sender, EventArgs e)
    {
        if (ItemOf<DeckCardItem>(sender!) is { } item) Vm.ShowCardMenuCommand.Execute(item);
    }
}
