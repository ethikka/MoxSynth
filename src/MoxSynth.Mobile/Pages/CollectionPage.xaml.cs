using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

public partial class CollectionPage : ViewModelPage
{
    public CollectionPage(CollectionViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private CollectionViewModel Vm => (CollectionViewModel)ViewModel;

    private void OnGroupTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<GroupItem>(sender!) is { } item) Vm.OpenCommand.Execute(item);
    }

    private void OnIncrementClicked(object? sender, EventArgs e)
    {
        if (ItemOf<GroupItem>(sender!) is { } item) Vm.IncrementCommand.Execute(item);
    }

    private void OnDecrementClicked(object? sender, EventArgs e)
    {
        if (ItemOf<GroupItem>(sender!) is { } item) Vm.DecrementCommand.Execute(item);
    }

    private void OnToggleScryfall(object? sender, EventArgs e) => Vm.ShowScryfall = !Vm.ShowScryfall;
}
