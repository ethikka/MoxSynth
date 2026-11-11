using System.Text.Json;
using MoxSynth.Client.Services;

namespace MoxSynth.Mobile.Services;

public sealed class PreferencesSettingsStore : ISettingsStore
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string? value)
    {
        if (value is null) Preferences.Default.Remove(key);
        else Preferences.Default.Set(key, value);
    }
}

/// <summary>Session tokens in SecureStorage (Android: encrypted with a Keystore key; iOS: the Keychain).</summary>
public sealed class SecureTokenStore : ITokenStore
{
    private const string Key = "moxsynth-session";

    public async Task<StoredSession?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? null : JsonSerializer.Deserialize<StoredSession>(json, MoxApiClient.Json);
        }
        catch (Exception)
        {
            // Keystore key lost (e.g. app data restored on another device) – start signed out.
            SecureStorage.Default.RemoveAll();
            return null;
        }
    }

    public async Task SaveAsync(StoredSession? session)
    {
        if (session is null)
            SecureStorage.Default.Remove(Key);
        else
            await SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(session, MoxApiClient.Json));
    }
}

public sealed class ShellNavigator : INavigator
{
    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() => parameters is null
            ? Shell.Current.GoToAsync(route)
            : Shell.Current.GoToAsync(route, parameters));

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));
}

public sealed class ShellDialogs : IDialogs
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel") =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlert(title, message, accept, cancel));

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel")
    {
        var choice = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.DisplayActionSheet(title, cancel, null, options.ToArray()));
        return choice is null || choice == cancel ? null : choice;
    }

    public Task ToastAsync(string message) => MainThread.InvokeOnMainThreadAsync(() =>
    {
#if ANDROID
        Android.Widget.Toast.MakeText(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity ?? Android.App.Application.Context, message,
            Android.Widget.ToastLength.Short)?.Show();
#elif IOS
        IosToast.Show(message);
#endif
    });
}

public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
}
