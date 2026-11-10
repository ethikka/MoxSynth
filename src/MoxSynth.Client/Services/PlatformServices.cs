using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

// Implemented by the app (MAUI) – keeps the view models free of UI framework types.

/// <summary>Small non-secret preferences (server address, price provider).</summary>
public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string? value);
}

/// <summary>Signed-in session (tokens) – stored encrypted by the platform (Android Keystore).</summary>
public interface ITokenStore
{
    Task<StoredSession?> LoadAsync();
    Task SaveAsync(StoredSession? session);
}

public sealed record StoredSession(
    string ServerUrl,
    long ProfileId,
    string ProfileName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpires);

public interface INavigator
{
    /// <summary>Navigates to a route (see <see cref="Routes"/>), optionally with parameters for the target view model.</summary>
    Task GoToAsync(string route, IDictionary<string, object>? parameters = null);
    Task GoBackAsync();
}

public static class Routes
{
    public const string Login = "//login";
    public const string Home = "//main/home";
    public const string Search = "//main/search";
    public const string Collection = "//main/collection";
    public const string History = "//main/history";
    public const string Decks = "//main/decks";
    public const string Card = "card";
    /// <summary>A deck (parameter <c>id</c>), pushed on the Decks tab.</summary>
    public const string Deck = "deck";
    public const string Settings = "settings";
}

public interface IDialogs
{
    Task AlertAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel");
    /// <summary>Returns the chosen option or null when cancelled.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel");
    /// <summary>Short non-blocking message.</summary>
    Task ToastAsync(string message);
}

/// <summary>Puts text on the system clipboard (the decklist export).</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Reads cards with on-device OCR: a live camera view, or a photo picked from the gallery.</summary>
public interface ICardScanner
{
    /// <summary>Picks (gallery) or takes (camera app) a photo and runs on-device OCR on it.</summary>
    /// <returns>OCR lines with bounding boxes, or null when the user cancelled.</returns>
    Task<IReadOnlyList<OcrLineDto>?> ScanAsync(bool fromGallery, CancellationToken ct = default);

    /// <summary>
    /// Live camera view; calls <paramref name="recognize"/> for each readable frame until a card is found.
    /// Returns the found result, <see cref="LiveScanOutcome.Gallery"/> when the user chose the gallery, or
    /// <see cref="LiveScanOutcome.Cancelled"/>.
    /// </summary>
    Task<LiveScanResult> ScanLiveAsync(Func<IReadOnlyList<OcrLineDto>, CancellationToken, Task<RecognizeResultDto>> recognize,
        CancellationToken ct = default);
}

public enum LiveScanOutcome
{
    Found,
    Gallery,
    Cancelled,
}

/// <summary>How a live scan ended; <see cref="Result"/> is set (and <c>Found</c>) only for <see cref="LiveScanOutcome.Found"/>.</summary>
public sealed record LiveScanResult(LiveScanOutcome Outcome, RecognizeResultDto? Result = null)
{
    public static readonly LiveScanResult Gallery = new(LiveScanOutcome.Gallery);
    public static readonly LiveScanResult Cancelled = new(LiveScanOutcome.Cancelled);
    public static LiveScanResult Found(RecognizeResultDto result) => new(LiveScanOutcome.Found, result);
}
