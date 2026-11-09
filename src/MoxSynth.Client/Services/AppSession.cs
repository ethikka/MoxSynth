using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

/// <summary>Signed-in profile, server facts and the chosen price provider (remembered on the device).</summary>
public sealed class AppSession(MoxApiClient api, ISettingsStore settings)
{
    public const string ServerUrlKey = "server-url";
    private const string ProviderKey = "price-provider";

    public MeDto? Me { get; private set; }

    public IReadOnlyList<PriceProviderDto> Providers => Me?.PriceProviders ?? [];

    public string? Provider { get; private set; }

    public PriceProviderDto? ProviderInfo => Providers.FirstOrDefault(p => p.Id == Provider);

    /// <summary>Raised when the price provider changes; pages reload their prices.</summary>
    public event Action? ProviderChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Me = await api.GetMeAsync(ct);
        var stored = settings.Get(ProviderKey);
        Provider = Me.PriceProviders.Any(p => p.Id == stored) ? stored : Me.DefaultPriceProvider;
    }

    public void SetProvider(string provider)
    {
        if (provider == Provider || Providers.All(p => p.Id != provider)) return;
        Provider = provider;
        settings.Set(ProviderKey, provider);
        ProviderChanged?.Invoke();
    }

    public async Task SignOutAsync()
    {
        Me = null;
        await api.SignOutAsync();
    }
}
