using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class HistoryPage : ViewModelPage
{
    public HistoryPage(HistoryViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<HistoryItem>(sender!) is { } item) ((HistoryViewModel)ViewModel).OpenCommand.Execute(item);
    }
}
