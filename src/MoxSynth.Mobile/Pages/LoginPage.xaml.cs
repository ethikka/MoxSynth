using MoxSynth.Client.ViewModels;
using MoxSynth.Contracts;

namespace MoxSynth.Mobile.Pages;

public partial class LoginPage : ViewModelPage
{
    public LoginPage(LoginViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
    }

    private void OnProfileTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf<ProfileDto>(sender!) is not { } profile) return;
        ((LoginViewModel)ViewModel).SelectProfileCommand.Execute(profile);
        PasswordEntry.Focus();
    }
}
