using MoxSynth.Client.Services;

namespace MoxSynth.Mobile.Pages;

/// <summary>First page: continue a stored session (straight to the tabs, also offline) or ask to sign in.</summary>
public sealed class StartPage : ContentPage
{
    private readonly MoxApiClient _api;
    private bool _started;

    public StartPage(MoxApiClient api)
    {
        _api = api;
        Shell.SetNavBarIsVisible(this, false);
        Content = new ActivityIndicator { IsRunning = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        var signedIn = await _api.RestoreAsync();
        await Shell.Current.GoToAsync(signedIn ? Routes.Home : Routes.Login);
    }
}
