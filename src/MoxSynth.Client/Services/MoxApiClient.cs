using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

/// <summary>Error returned by the MoxSynth server (or a connection problem: <see cref="StatusCode"/> 0).</summary>
public sealed class ApiException(HttpStatusCode statusCode, string code, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// Typed client for the MoxSynth /api. Holds the session (bearer tokens): refreshes the access token shortly before
/// it expires or after a 401, and raises <see cref="SessionExpired"/> when the refresh token is no longer accepted
/// (expired, or the password was changed).
/// </summary>
public sealed class MoxApiClient(ITokenStore tokens, Func<HttpMessageHandler>? handlerFactory = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Server address used until another one is entered (host name "moxsynth" on the home network).</summary>
    public const string DefaultServer = "moxsynth:5180";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HttpClient? _http;
    private string? _baseUrl;
    private StoredSession? _session;

    public StoredSession? Session => _session;
    public bool IsSignedIn => _session is not null;

    public event Action? SessionExpired;

    /// <summary>"192.168.1.20:5180" → "http://192.168.1.20:5180/". Null when it isn't a usable address.</summary>
    public static string? NormalizeServerUrl(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    /// <summary>Restores a stored session (app start). Returns false when there is none.</summary>
    public async Task<bool> RestoreAsync()
    {
        var session = await tokens.LoadAsync();
        if (session is null) return false;
        UseServer(session.ServerUrl);
        _session = session;
        return true;
    }

    public void UseServer(string serverUrl)
    {
        var url = NormalizeServerUrl(serverUrl) ?? throw new ArgumentException("Invalid server address.", nameof(serverUrl));
        if (url == _baseUrl && _http is not null) return;
        _http?.Dispose();
        _http = new HttpClient(handlerFactory?.Invoke() ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _baseUrl = url;
    }

    // ------------------------------------------------------------ sign-in

    public Task<List<ProfileDto>> GetProfilesAsync(CancellationToken ct = default) =>
        SendAsync<List<ProfileDto>>(HttpMethod.Get, "api/profiles", null, authenticated: false, ct);

    public async Task SignInAsync(ProfileDto profile, string password, CancellationToken ct = default)
    {
        var token = await SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/login",
            new LoginRequest(profile.Id, password), authenticated: false, ct);
        await StoreAsync(profile.Id, profile.Name, token);
    }

    public async Task SignOutAsync()
    {
        _session = null;
        await tokens.SaveAsync(null);
    }

    private async Task StoreAsync(long profileId, string profileName, TokenResponse token)
    {
        _session = new StoredSession(_baseUrl!, profileId, profileName, token.AccessToken, token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        await tokens.SaveAsync(_session);
    }

    /// <summary>Gets a new access token with the refresh token; false (and signed out) when it was refused.</summary>
    private async Task<bool> RefreshAsync(string usedAccessToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var session = _session;
            if (session is null) return false;
            if (session.AccessToken != usedAccessToken) return true; // another request already refreshed

            try
            {
                var token = await SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/refresh",
                    new RefreshRequest(session.RefreshToken), authenticated: false, ct);
                await StoreAsync(session.ProfileId, session.ProfileName, token);
                return true;
            }
            catch (ApiException ex) when (ex.IsUnauthorized)
            {
                await SignOutAsync();
                SessionExpired?.Invoke();
                return false;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ------------------------------------------------------------ data

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => GetAsync<MeDto>("api/me", ct);

    public Task<StatsDto> GetStatsAsync(string? provider, CancellationToken ct = default) =>
        GetAsync<StatsDto>("api/stats" + Query(("provider", provider)), ct);

    public Task<PageDto<HistoryEventDto>> GetHistoryAsync(int page, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<PageDto<HistoryEventDto>>("api/history" + Query(("page", I(page)), ("pageSize", I(pageSize))), ct);

    public Task<PageDto<CardSummaryDto>> SearchCardsAsync(string? query, string? scryfall, string? provider, int page,
        int pageSize = 30, CancellationToken ct = default) =>
        GetAsync<PageDto<CardSummaryDto>>("api/cards/search" + Query(("q", query), ("sf", scryfall), ("provider", provider),
            ("page", I(page)), ("pageSize", I(pageSize))), ct);

    public Task<CardDetailDto> GetCardAsync(string uuid, string? provider, CancellationToken ct = default) =>
        GetAsync<CardDetailDto>($"api/cards/{Uri.EscapeDataString(uuid)}" + Query(("provider", provider)), ct);

    public Task<RecognizeResultDto> RecognizeAsync(IReadOnlyList<OcrLineDto> lines, string? provider, CancellationToken ct = default) =>
        SendAsync<RecognizeResultDto>(HttpMethod.Post, "api/cards/recognize" + Query(("provider", provider)),
            new RecognizeRequest(lines), authenticated: true, ct);

    public Task<CollectionPageDto> GetCollectionAsync(string? query, string? scryfall, string? sort, string? provider,
        int page, int pageSize = 50, bool includeRemoved = false, CancellationToken ct = default) =>
        GetAsync<CollectionPageDto>("api/collection" + Query(("q", query), ("sf", scryfall), ("sort", sort),
            ("provider", provider), ("removed", includeRemoved ? "true" : null), ("page", I(page)), ("pageSize", I(pageSize))), ct);

    public Task<AddCardsResultDto> AddCardsAsync(AddCardsDto request, CancellationToken ct = default) =>
        SendAsync<AddCardsResultDto>(HttpMethod.Post, "api/collection", request, authenticated: true, ct);

    public Task RemoveOneAsync(RemoveOneDto request, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, "api/collection/remove-one", request, authenticated: true, ct);

    public Task RemoveCopyAsync(long id, string? reason, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/collection/{I(id)}" + Query(("reason", reason)), null, authenticated: true, ct);

    public Task RestoreCopyAsync(long id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, $"api/collection/{I(id)}/restore", null, authenticated: true, ct);

    // ------------------------------------------------------------ decks

    public Task<List<DeckSummaryDto>> GetDecksAsync(string? provider, CancellationToken ct = default) =>
        GetAsync<List<DeckSummaryDto>>("api/decks" + Query(("provider", provider)), ct);

    public Task<List<DeckFormatDto>> GetDeckFormatsAsync(CancellationToken ct = default) =>
        GetAsync<List<DeckFormatDto>>("api/decks/formats", ct);

    public Task<DeckDetailDto> CreateDeckAsync(CreateDeckRequest request, string? provider, CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Post, "api/decks" + Query(("provider", provider)), request, authenticated: true, ct);

    public Task<DeckDetailDto> GetDeckAsync(long deckId, string? provider, CancellationToken ct = default) =>
        GetAsync<DeckDetailDto>(DeckPath(deckId) + Query(("provider", provider)), ct);

    public Task<DeckDetailDto> UpdateDeckAsync(long deckId, UpdateDeckRequest request, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Put, DeckPath(deckId) + Query(("provider", provider)), request, authenticated: true, ct);

    public Task DeleteDeckAsync(long deckId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, DeckPath(deckId), null, authenticated: true, ct);

    /// <summary>Copies the deck; returns the new deck.</summary>
    public Task<DeckDetailDto> DuplicateDeckAsync(long deckId, string? provider, CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Post, DeckPath(deckId, "/duplicate") + Query(("provider", provider)), null,
            authenticated: true, ct);

    public Task<DeckDetailDto> AddDeckCardAsync(long deckId, AddDeckCardRequest request, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Post, DeckPath(deckId, "/cards") + Query(("provider", provider)), request,
            authenticated: true, ct);

    /// <summary>Sets a card's quantity; 0 removes it from the deck.</summary>
    public Task<DeckDetailDto> SetDeckCardQuantityAsync(long deckId, long deckCardId, int quantity, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Put, DeckPath(deckId, $"/cards/{I(deckCardId)}/quantity") + Query(("provider", provider)),
            new SetDeckCardQuantityRequest(quantity), authenticated: true, ct);

    /// <summary>Replaces a card's categories (names, primary first; empty = its type category).</summary>
    public Task<DeckDetailDto> SetDeckCardCategoriesAsync(long deckId, long deckCardId, IReadOnlyList<string> categories,
        string? provider, CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Put, DeckPath(deckId, $"/cards/{I(deckCardId)}/categories") + Query(("provider", provider)),
            new SetDeckCardCategoriesRequest(categories), authenticated: true, ct);

    public Task<DeckDetailDto> ChangeDeckCardPrintingAsync(long deckId, long deckCardId, ChangeDeckCardPrintingRequest request,
        string? provider, CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Put, DeckPath(deckId, $"/cards/{I(deckCardId)}/printing") + Query(("provider", provider)),
            request, authenticated: true, ct);

    public Task<DeckDetailDto> AddDeckCategoryAsync(long deckId, CreateDeckCategoryRequest request, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Post, DeckPath(deckId, "/categories") + Query(("provider", provider)), request,
            authenticated: true, ct);

    /// <summary>Renames, (un)counts and/or moves a category (only the fields that are set).</summary>
    public Task<DeckDetailDto> UpdateDeckCategoryAsync(long deckId, long categoryId, UpdateDeckCategoryRequest request,
        string? provider, CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Put, DeckPath(deckId, $"/categories/{I(categoryId)}") + Query(("provider", provider)),
            request, authenticated: true, ct);

    public Task<DeckDetailDto> DeleteDeckCategoryAsync(long deckId, long categoryId, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckDetailDto>(HttpMethod.Delete, DeckPath(deckId, $"/categories/{I(categoryId)}") + Query(("provider", provider)),
            null, authenticated: true, ct);

    public Task<DeckImportResultDto> ImportDeckTextAsync(long deckId, string text, bool replace, string? provider,
        CancellationToken ct = default) =>
        SendAsync<DeckImportResultDto>(HttpMethod.Post, DeckPath(deckId, "/import") + Query(("provider", provider)),
            new ImportDeckTextRequest(text, replace), authenticated: true, ct);

    public async Task<string> ExportDeckTextAsync(long deckId, CancellationToken ct = default) =>
        (await GetAsync<DeckTextDto>(DeckPath(deckId, "/export"), ct)).Text;

    private static string DeckPath(long deckId, string suffix = "") => $"api/decks/{I(deckId)}{suffix}";

    // ------------------------------------------------------------ plumbing

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, authenticated: true, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("No server chosen.");

        if (authenticated)
        {
            var session = _session ?? throw new ApiException(HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized, "Not signed in.");
            if (session.AccessTokenExpires <= DateTimeOffset.UtcNow.AddMinutes(1))
                await RefreshAsync(session.AccessToken, ct);
        }

        for (var attempt = 0; ; attempt++)
        {
            var accessToken = _session?.AccessToken;
            using var request = new HttpRequestMessage(method, path);
            if (authenticated)
            {
                if (accessToken is null)
                    throw new ApiException(HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized, "Please sign in again.");
                request.Headers.Authorization = new("Bearer", accessToken);
            }
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(0, "connection", $"Can't reach the MoxSynth server at {_baseUrl} ({ex.Message}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ApiException(0, "timeout", $"The MoxSynth server at {_baseUrl} didn't answer in time.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && attempt == 0
                    && await RefreshAsync(accessToken!, ct))
                    continue; // retry once with the new token

                if (!response.IsSuccessStatusCode)
                    throw await ToExceptionAsync(response, ct);

                if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
                return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
            }
        }
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct);
            if (error is not null) return new ApiException(response.StatusCode, error.Code, error.Message);
        }
        catch (Exception)
        {
            // not a MoxSynth error body (e.g. wrong server or proxy page)
        }
        return new ApiException(response.StatusCode, "http", $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Query(params (string Key, string? Value)[] parameters)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }
        return sb.ToString();
    }
}
