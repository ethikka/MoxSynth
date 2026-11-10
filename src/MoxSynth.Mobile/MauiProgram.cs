using MoxSynth.Client.Services;
using MoxSynth.Client.ViewModels;
using MoxSynth.Mobile.Controls;
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
            .ConfigureMauiHandlers(handlers => handlers.AddHandler<CameraPreview, CameraPreviewHandler>())
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
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<ICardScanner, MlKitCardScanner>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());

        // API + session (one per app)
        services.AddSingleton(sp => new MoxApiClient(sp.GetRequiredService<ITokenStore>()));
        services.AddSingleton<AppSession>();
        services.AddSingleton<DeckFormatCatalog>();

        // Tab pages keep their state (singletons); pushed pages are new each time.
        services.AddSingleton<LoginViewModel>().AddSingleton<LoginPage>();
        services.AddSingleton<HomeViewModel>().AddSingleton<HomePage>();
        services.AddSingleton<SearchViewModel>().AddSingleton<SearchPage>();
        services.AddSingleton<CollectionViewModel>().AddSingleton<CollectionPage>();
        services.AddSingleton<HistoryViewModel>().AddSingleton<HistoryPage>();
        services.AddSingleton<DecksViewModel>().AddSingleton<DecksPage>();
        services.AddTransient<DeckViewModel>().AddTransient<DeckPage>();
        services.AddTransient<CardDetailViewModel>().AddTransient<CardDetailPage>();
        services.AddTransient<SettingsViewModel>().AddTransient<SettingsPage>();
        services.AddTransient<StartPage>();
        services.AddTransient<LiveScanPage>(); // pushed modally by MlKitCardScanner, a new camera view each scan

        return builder.Build();
    }
}
