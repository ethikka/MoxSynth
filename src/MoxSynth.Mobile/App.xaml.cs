using MoxSynth.Client.Services;
using MoxSynth.Mobile.Services;

namespace MoxSynth.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly ThemeService _themes;

    public App(IServiceProvider services, MoxApiClient api, IDialogs dialogs, ThemeService themes)
    {
        InitializeComponent();
        _services = services;
        _themes = themes;

        // Theme chosen on this device (Settings), same themes as the web app.
        themes.Initialize(this);

        // Refresh token refused (expired, or the password was changed): back to the sign-in screen.
        api.SessionExpired += () => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Shell.Current.GoToAsync(Routes.Login);
            await dialogs.ToastAsync("Your session has expired – please sign in again.");
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell()) { Title = "MoxSynth" };
        window.Activated += (_, _) => _themes.RefreshStatusBar();
        return window;
    }
}
