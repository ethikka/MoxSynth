using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

/// <summary>Signed-in profile, server facts and the chosen price provider (remembered on the device).</summary>
public sealed class AppSession
{
    private readonly MoxApiClient api;
    private readonly ISettingsStore settings;

    public AppSession(MoxApiClient api, ISettingsStore settings)
    {
        this.api = api;
        this.settings = settings;
        // The API client already signed itself out; drop the profile and let the pages forget its data.
        // (MainThread is not reachable from this plain net10.0 library, so this is raised on the caller's thread.)
        api.SessionExpired += () =>
        {
            Me = null;
            SignedOut?.Invoke();
        };
    }

    public const string ServerUrlKey = "server-url";
    private const string ProviderKey = "price-provider";

    public MeDto? Me { get; private set; }

    public IReadOnlyList<PriceProviderDto> Providers => Me?.PriceProviders ?? [];

    public string? Provider { get; private set; }

    public PriceProviderDto? ProviderInfo => Providers.FirstOrDefault(p => p.Id == Provider);

    /// <summary>Raised when the price provider changes; pages reload their prices.</summary>
    public event Action? ProviderChanged;

    /// <summary>Raised after signing out (or when the session expired); pages drop the previous account's data.</summary>
    public event Action? SignedOut;

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
        SignedOut?.Invoke();
    }
}
