using MoxSynth.Client.Services;

namespace MoxSynth.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services, MoxApiClient api, IDialogs dialogs)
    {
        InitializeComponent();
        _services = services;

        // Refresh token refused (expired, or the password was changed): back to the sign-in screen.
        api.SessionExpired += () => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Shell.Current.GoToAsync(Routes.Login);
            await dialogs.ToastAsync("Your session has expired – please sign in again.");
        });
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new AppShell()) { Title = "MoxSynth" };
}
