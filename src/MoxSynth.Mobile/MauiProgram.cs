using MoxSynth.Client.Services;
using MoxSynth.Client.ViewModels;
using MoxSynth.Mobile.Pages;
using MoxSynth.Mobile.Services;
using Plugin.Maui.OCR;

namespace MoxSynth.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseOcr()
            .ConfigureFonts(fonts =>
            {
                // Heading fonts of the special themes (same as the web app; SIL Open Font License).
                fonts.AddFont("Orbitron.ttf", "Orbitron");
                fonts.AddFont("Creepster-Regular.ttf", "Creepster");
                fonts.AddFont("Cinzel.ttf", "Cinzel");
                fonts.AddFont("Rajdhani-SemiBold.ttf", "Rajdhani");
            });

        var services = builder.Services;

        // Platform services
        services.AddSingleton<IOcrService>(_ => OcrPlugin.Default);
        services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();
        services.AddSingleton<ITokenStore, SecureTokenStore>();
        services.AddSingleton<INavigator, ShellNavigator>();
        services.AddSingleton<IDialogs, ShellDialogs>();
        services.AddSingleton<ICardScanner, MlKitCardScanner>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());

        // API + session (one per app)
        services.AddSingleton(sp => new MoxApiClient(sp.GetRequiredService<ITokenStore>()));
        services.AddSingleton<AppSession>();

        // Tab pages keep their state (singletons); pushed pages are new each time.
        services.AddSingleton<LoginViewModel>().AddSingleton<LoginPage>();
        services.AddSingleton<HomeViewModel>().AddSingleton<HomePage>();
        services.AddSingleton<SearchViewModel>().AddSingleton<SearchPage>();
        services.AddSingleton<CollectionViewModel>().AddSingleton<CollectionPage>();
        services.AddSingleton<HistoryViewModel>().AddSingleton<HistoryPage>();
        services.AddTransient<CardDetailViewModel>().AddTransient<CardDetailPage>();
        services.AddTransient<SettingsViewModel>().AddTransient<SettingsPage>();
        services.AddTransient<StartPage>();

        return builder.Build();
    }
}
