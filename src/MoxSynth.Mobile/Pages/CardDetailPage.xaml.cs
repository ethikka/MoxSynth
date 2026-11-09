using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class CardDetailPage : ViewModelPage
{
    public CardDetailPage(CardDetailViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private CardDetailViewModel Vm => (CardDetailViewModel)ViewModel;

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (ItemOf<CopyItem>(sender!) is { } item) Vm.RemoveCopyCommand.Execute(item);
    }

    private void OnRestoreClicked(object? sender, EventArgs e)
    {
        if (ItemOf<CopyItem>(sender!) is { } item) Vm.RestoreCopyCommand.Execute(item);
    }

    private void OnPrintingTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<CardItem>(sender!) is { } item) Vm.OpenPrintingCommand.Execute(item);
    }
}
