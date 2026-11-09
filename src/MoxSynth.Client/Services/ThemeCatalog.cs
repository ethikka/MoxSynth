namespace MoxSynth.Client.Services;

/// <summary>Colours of one theme (hex). Mirrors the web app's themes (wwwroot/app.css + themes.css).</summary>
public sealed record ThemePalette(
    string PageTop,
    string PageBottom,
    string Surface,
    string Surface2,
    string Border,
    string Text,
    string Heading,
    string Muted,
    string Accent,
    string OnAccent,
    string Link,
    string Ok,
    string Bad,
    string NavBackground,
    string NavForeground,
    string NavAccent,
    string InputBackground,
    /// <summary>Font alias registered by the app (Orbitron, Creepster, Cinzel, Rajdhani); null = system font.</summary>
    string? HeadingFont = null,
    bool UppercaseHeadings = false,
    int Radius = 8);

/// <summary>A selectable theme. <see cref="Palette"/> is null for the automatic ones (system / seasonal).</summary>
public sealed record ThemeDefinition(string Id, string Label, string Group, bool IsDark, ThemePalette? Palette)
{
    public override string ToString() => Label; // pickers
}

/// <summary>Applies a theme to the UI (implemented by the app).</summary>
public interface IThemeService
{
    /// <summary>The chosen theme id (may be "system" or "seasonal").</summary>
    string Current { get; }

    void Apply(string themeId);
}

/// <summary>The same themes as the web app: Basic, Seasons (+ automatic by month), Special.</summary>
public static class ThemeCatalog
{
    public const string SettingsKey = "theme";
    public const string System = "system";
    public const string Seasonal = "seasonal";

    private static readonly ThemePalette Light = new(
        "#F6F7F9", "#F6F7F9", "#FFFFFF", "#F1F3F5", "#DEE2E6", "#212529", "#212529", "#6C757D",
        "#0D6EFD", "#FFFFFF", "#0D6EFD", "#198754", "#DC3545", "#212529", "#FFFFFF", "#0D6EFD", "#FFFFFF");

    private static readonly ThemePalette Dark = new(
        "#16181C", "#16181C", "#212529", "#2B3035", "#3D434A", "#DEE2E6", "#F8F9FA", "#ADB5BD",
        "#3D8BFD", "#FFFFFF", "#6EA8FE", "#75B798", "#EA868F", "#0F1114", "#F8F9FA", "#3D8BFD", "#1A1D21");

    public static readonly IReadOnlyList<ThemeDefinition> All =
    [
        new(System, "◐ System", "Basic", false, null),
        new("light", "☀ Light", "Basic", false, Light),
        new("dark", "☾ Dark", "Basic", true, Dark),

        new(Seasonal, "↻ Season (automatic)", "Seasons", false, null),
        new("spring", "✿ Spring", "Seasons", false, new(
            "#FBE3EC", "#E3F5E7", "#FFFFFF", "#EEF7EF", "#D5E8D8", "#22372A", "#1F5133", "#5D7566",
            "#2F9E5A", "#FFFFFF", "#C2407A", "#2F9E5A", "#D6336C", "#2F6B45", "#FFFFFF", "#F7A8C4", "#FFFFFF",
            Radius: 12)),
        new("summer", "☼ Summer", "Seasons", false, new(
            "#D4EFFF", "#FFF4DA", "#FFFFFF", "#F1F8FD", "#CFE4F1", "#23323F", "#0B5C8F", "#5B6F80",
            "#0C8ED9", "#FFFFFF", "#E8590C", "#2B9348", "#E03131", "#0B6AA2", "#FFFFFF", "#FFC94D", "#FFFFFF",
            Radius: 10)),
        new("autumn", "❦ Autumn", "Seasons", false, new(
            "#F8E4CC", "#FBF3E6", "#FFFBF4", "#F6E8D6", "#E8D2B8", "#3B2A1E", "#7A3410", "#7D6656",
            "#C2410C", "#FFFFFF", "#8A4B12", "#5C8A1E", "#B42318", "#5B2C0F", "#FFFFFF", "#D4A017", "#FFFDF8",
            HeadingFont: "serif")),
        new("winter", "❄ Winter", "Seasons", false, new(
            "#CFE2F3", "#EAF2FA", "#FFFFFF", "#EEF4FA", "#C9DCEF", "#1F2F40", "#1C4A78", "#5B7088",
            "#2B6CB0", "#FFFFFF", "#3C5A99", "#2B8A6E", "#C92A2A", "#1F3B5C", "#FFFFFF", "#BFE0FF", "#FFFFFF")),

        new("neon", "⚡ Neon", "Special", true, new(
            "#1A0B33", "#0B0716", "#140D26", "#1D1335", "#3A2566", "#E9E4FF", "#FF2BD6", "#A99BD8",
            "#FF2BD6", "#0B0716", "#00F0FF", "#A6FF00", "#FF4D8D", "#0B0716", "#E9E4FF", "#00F0FF", "#0F0920",
            HeadingFont: "Orbitron", Radius: 6)),
        new("halloween", "☠ Halloween", "Special", true, new(
            "#2A1236", "#120A16", "#1E1024", "#2A1532", "#4A2A57", "#EADFF0", "#FF9A3C", "#A893B3",
            "#FF7518", "#1A0D00", "#C39BFF", "#8BD34A", "#FF4D6D", "#0D0610", "#FF7518", "#FF7518", "#170C1C",
            HeadingFont: "Creepster")),
        new("steampunk", "⚙ Steampunk", "Special", true, new(
            "#33240F", "#1D140C", "#2B1E12", "#382816", "#7A5A2E", "#EFE2C8", "#E3B766", "#B39C7A",
            "#C8963E", "#1D140C", "#E09A5C", "#9BBF5A", "#E0644E", "#2E1F10", "#E3B766", "#C8963E", "#241910",
            HeadingFont: "Cinzel", Radius: 3)),
        new("cyberpunk", "⌁ Cyberpunk", "Special", true, new(
            "#2A0E1E", "#0B0B10", "#12121C", "#1B1B2A", "#2E3350", "#E6E8F0", "#FCEE0A", "#8A90A6",
            "#FCEE0A", "#0B0B10", "#00F0FF", "#00FF9C", "#FF2A6D", "#050508", "#FCEE0A", "#FF2A6D", "#0E0E16",
            HeadingFont: "Rajdhani", UppercaseHeadings: true, Radius: 0)),
    ];

    public static ThemeDefinition Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>Meteorological seasons (northern hemisphere), same as the web app.</summary>
    public static string SeasonOf(DateTime date) => date.Month switch
    {
        >= 3 and <= 5 => "spring",
        >= 6 and <= 8 => "summer",
        >= 9 and <= 11 => "autumn",
        _ => "winter",
    };

    /// <summary>The concrete theme to show for a choice ("system" follows the OS, "seasonal" the month).</summary>
    public static ThemeDefinition Resolve(string? id, bool systemIsDark, DateTime today) => Get(id).Id switch
    {
        System => Get(systemIsDark ? "dark" : "light"),
        Seasonal => Get(SeasonOf(today)),
        var other => Get(other),
    };
}
