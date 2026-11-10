using MoxSynth.Client.ViewModels;
using MoxSynth.Contracts;

namespace MoxSynth.Mobile.Pages;

public partial class LoginPage : ViewModelPage
{
    public LoginPage(LoginViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!((LoginViewModel)ViewModel).TakeFocusServerRequest()) return;

        // Change-server mode: let the user edit the current address right away.
        Dispatcher.Dispatch(() =>
        {
            ServerEntry.Focus();
            ServerEntry.CursorPosition = 0;
            ServerEntry.SelectionLength = ServerEntry.Text?.Length ?? 0;
        });
    }

    private void OnProfileTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<ProfileDto>(sender!) is not { } profile) return;
        ((LoginViewModel)ViewModel).SelectProfileCommand.Execute(profile);
        PasswordEntry.Focus();
    }
}
