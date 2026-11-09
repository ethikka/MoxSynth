using MoxSynth.Client.Services;

namespace MoxSynth.Mobile.Services;

/// <summary>
/// Puts the palette of the chosen theme (<see cref="ThemeCatalog"/>, same themes as the web app) into the app
/// resources. All styles and pages use these keys as DynamicResource, so a switch takes effect immediately.
/// "system" follows the phone's light/dark setting, "seasonal" the current month.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsStore _settings;
    private ResourceDictionary? _applied;
    private Application? _app;
    private (Color Color, bool Dark)? _statusBar;

    public ThemeService(ISettingsStore settings)
    {
        _settings = settings;
        Current = ThemeCatalog.Get(settings.Get(ThemeCatalog.SettingsKey)).Id;
    }

    public string Current { get; private set; }

    /// <summary>Applies the stored theme (app start) and follows OS light/dark changes for "system".</summary>
    public void Initialize(Application app)
    {
        _app = app;
        Apply(Current);
        app.RequestedThemeChanged += (_, _) =>
        {
            if (Current == ThemeCatalog.System) Apply(Current);
        };
    }

    public void Apply(string themeId)
    {
        var app = _app ?? Application.Current;
        if (app is null) return;

        Current = ThemeCatalog.Get(themeId).Id;
        _settings.Set(ThemeCatalog.SettingsKey, Current);

        var systemDark = app.PlatformAppTheme == AppTheme.Dark;
        var theme = ThemeCatalog.Resolve(Current, systemDark, DateTime.Now);
        var p = theme.Palette!;

        var resources = new ResourceDictionary
        {
            ["ThemePageBackground"] = C(p.PageBottom),
            ["ThemePageBrush"] = new LinearGradientBrush(
                [new GradientStop(C(p.PageTop), 0f), new GradientStop(C(p.PageBottom), 0.45f)],
                new Point(0, 0), new Point(0, 1)),
            ["ThemeSurface"] = C(p.Surface),
            ["ThemeSurface2"] = C(p.Surface2),
            ["ThemeBorder"] = C(p.Border),
            ["ThemeText"] = C(p.Text),
            ["ThemeHeading"] = C(p.Heading),
            ["ThemeMuted"] = C(p.Muted),
            ["ThemeAccent"] = C(p.Accent),
            ["ThemeOnAccent"] = C(p.OnAccent),
            ["ThemeLink"] = C(p.Link),
            ["ThemeOk"] = C(p.Ok),
            ["ThemeBad"] = C(p.Bad),
            ["ThemeNavBackground"] = C(p.NavBackground),
            ["ThemeNavForeground"] = C(p.NavForeground),
            ["ThemeNavAccent"] = C(p.NavAccent),
            ["ThemeInputBackground"] = C(p.InputBackground),
            ["ThemeHeadingFont"] = p.HeadingFont ?? "",
            ["ThemeHeadingTransform"] = p.UppercaseHeadings ? TextTransform.Uppercase : TextTransform.None,
            ["ThemeCornerRadius"] = p.Radius,
        };

        var merged = app.Resources.MergedDictionaries;
        if (_applied is not null) merged.Remove(_applied);
        merged.Add(resources);
        _applied = resources;

        // Native controls (pickers, dialogs, keyboard) follow the theme's light/dark base.
        app.UserAppTheme = theme.IsDark ? AppTheme.Dark : AppTheme.Light;
        _statusBar = (C(p.NavBackground), theme.IsDark || IsDarkColor(p.NavBackground));
        RefreshStatusBar();
    }

    /// <summary>Status bar in the navbar colour (again once the activity exists / comes back).</summary>
    public void RefreshStatusBar()
    {
        if (_statusBar is { } bar) SetStatusBar(bar.Color, bar.Dark);
    }

    private static Color C(string hex) => Color.FromArgb(hex);

    private static bool IsDarkColor(string hex)
    {
        var c = C(hex);
        return 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue < 0.5;
    }

    private static void SetStatusBar(Color color, bool darkBar)
    {
#if ANDROID
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window is null) return;
#pragma warning disable CA1422 // deprecated on Android 15 (edge-to-edge) but still honoured before that
            window.SetStatusBarColor(Android.Graphics.Color.Argb(255, (int)(color.Red * 255), (int)(color.Green * 255), (int)(color.Blue * 255)));
#pragma warning restore CA1422
            const int lightStatusBars = 8; // WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                window.InsetsController?.SetSystemBarsAppearance(darkBar ? 0 : lightStatusBars, lightStatusBars);
        }
        catch (Exception)
        {
            // cosmetic only
        }
#endif
    }
}
