using MoxSynth.Client.Services;
using MoxSynth.Mobile.Pages;

namespace MoxSynth.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        // Pages pushed on top of a tab (resolved from DI, see MauiProgram).
        Routing.RegisterRoute(Routes.Card, typeof(CardDetailPage));
        Routing.RegisterRoute(Routes.Settings, typeof(SettingsPage));
    }
}
