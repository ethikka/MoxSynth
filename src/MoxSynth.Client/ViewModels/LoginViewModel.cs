using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>Server address → profile picker → password.</summary>
public sealed partial class LoginViewModel(MoxApiClient api, AppSession session, ISettingsStore settings, INavigator nav)
    : ViewModelBase
{
    [ObservableProperty]
    private string _serverUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProfile))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private ProfileDto? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    [ObservableProperty]
    private bool _isConnected;

    public ObservableCollection<ProfileDto> Profiles { get; } = [];

    public bool HasSelectedProfile => SelectedProfile is not null;

    public override async Task OnAppearingAsync()
    {
        if (string.IsNullOrEmpty(ServerUrl)) ServerUrl = settings.Get(AppSession.ServerUrlKey) ?? MoxApiClient.DefaultServer;
        if (!IsConnected && !string.IsNullOrEmpty(ServerUrl)) await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var url = MoxApiClient.NormalizeServerUrl(ServerUrl);
        if (url is null)
        {
            Error = "Enter the address of the MoxSynth server, e.g. 192.168.1.20:5180.";
            return;
        }

        IsConnected = false;
        Profiles.Clear();
        SelectedProfile = null;
        await RunAsync(async () =>
        {
            api.UseServer(url);
            var profiles = await api.GetProfilesAsync();
            foreach (var p in profiles) Profiles.Add(p);
            ServerUrl = url.TrimEnd('/');
            settings.Set(AppSession.ServerUrlKey, ServerUrl);
            IsConnected = true;
            if (profiles.Count == 0) Error = "There are no profiles yet – create the first one in the web app.";
        });
    }

    [RelayCommand]
    private void SelectProfile(ProfileDto profile)
    {
        SelectedProfile = profile;
        Password = "";
        Error = null;
    }

    private bool CanSignIn() => SelectedProfile is not null && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        var ok = await RunAsync(async () =>
        {
            await api.SignInAsync(SelectedProfile!, Password);
            await session.LoadAsync();
        });
        if (!ok) return;

        Password = "";
        await nav.GoToAsync(Routes.Home);
    }
}
