using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>Price shop, server info and signing out.</summary>
public sealed partial class SettingsViewModel(MoxApiClient api, AppSession session, INavigator nav, IDialogs dialogs)
    : ViewModelBase
{
    [ObservableProperty]
    private PriceProviderDto? _selectedProvider;

    public IReadOnlyList<PriceProviderDto> Providers => session.Providers;
    public string ProfileName => session.Me?.Profile.Name ?? "";
    public string ServerUrl => api.Session?.ServerUrl.TrimEnd('/') ?? "";
    public string CardDatabase => session.Me?.CardDatabaseDate is { } d ? $"MTGJSON {d}" : "–";
    public string PricesDate => session.Me?.PricesDate ?? "no prices imported";

    public override async Task OnAppearingAsync()
    {
        if (session.Me is null) await RunAsync(() => session.LoadAsync());
        OnPropertyChanged(string.Empty); // everything
        SelectedProvider = session.ProviderInfo;
    }

    partial void OnSelectedProviderChanged(PriceProviderDto? value)
    {
        if (value is not null) session.SetProvider(value.Id);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (!await dialogs.ConfirmAsync("Sign out", $"Sign out {ProfileName} on this device?", "Sign out")) return;
        await session.SignOutAsync();
        await nav.GoToAsync(Routes.Login);
    }
}
