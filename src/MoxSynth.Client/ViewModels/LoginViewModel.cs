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

    /// <summary>Settings key for the recently used servers (newline separated, most recent first).</summary>
    public const string RecentServersKey = "recent-servers";

    private const int MaxRecentServers = 5;

    private bool _recentServersLoaded;
    private bool _suppressAutoConnect;
    private bool _focusServerRequested;

    public ObservableCollection<ProfileDto> Profiles { get; } = [];

    /// <summary>Normalized addresses of servers connected to before, most recent first.</summary>
    public ObservableCollection<string> RecentServers { get; } = [];

    public bool HasSelectedProfile => SelectedProfile is not null;

    public bool HasRecentServers => RecentServers.Count > 0;

    /// <summary>
    /// True once after switching to change-server mode: the page should focus and select the server address.
    /// Reading it clears the request.
    /// </summary>
    public bool TakeFocusServerRequest()
    {
        var requested = _focusServerRequested;
        _focusServerRequested = false;
        return requested;
    }

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("changeServer", out var value) || value is not true) return;

        IsConnected = false;
        Profiles.Clear();
        SelectedProfile = null;
        Password = "";
        Error = null;
        if (string.IsNullOrEmpty(ServerUrl)) ServerUrl = settings.Get(AppSession.ServerUrlKey) ?? "";
        _suppressAutoConnect = true;
        _focusServerRequested = true;
    }

    public override async Task OnAppearingAsync()
    {
        LoadRecentServers();
        if (string.IsNullOrEmpty(ServerUrl)) ServerUrl = settings.Get(AppSession.ServerUrlKey) ?? MoxApiClient.DefaultServer;
        if (_suppressAutoConnect)
        {
            _suppressAutoConnect = false;
            return;
        }
        if (!IsConnected && !string.IsNullOrEmpty(ServerUrl)) await ConnectAsync();
    }

    private void LoadRecentServers()
    {
        if (_recentServersLoaded) return;
        _recentServersLoaded = true;

        var stored = settings.Get(RecentServersKey);
        IEnumerable<string> urls = stored is null
            ? [settings.Get(AppSession.ServerUrlKey) ?? ""] // first use: seed with the saved server
            : stored.Split('\n');
        foreach (var url in urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(MaxRecentServers))
            RecentServers.Add(url);
        if (stored is null) SaveRecentServers();
        OnPropertyChanged(nameof(HasRecentServers));
    }

    private void RememberServer(string url)
    {
        LoadRecentServers();
        var existing = RecentServers.FirstOrDefault(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentServers.Remove(existing);
        RecentServers.Insert(0, url);
        while (RecentServers.Count > MaxRecentServers) RecentServers.RemoveAt(RecentServers.Count - 1);
        SaveRecentServers();
        OnPropertyChanged(nameof(HasRecentServers));
    }

    private void SaveRecentServers() =>
        settings.Set(RecentServersKey, RecentServers.Count > 0 ? string.Join('\n', RecentServers) : null);

    [RelayCommand]
    private async Task UseRecentServerAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        ServerUrl = url;
        await ConnectAsync();
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
            RememberServer(ServerUrl);
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
