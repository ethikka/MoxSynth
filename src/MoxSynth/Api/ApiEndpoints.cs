using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.Extensions.Options;
using MoxSynth.Cards;
using MoxSynth.Collection;
using MoxSynth.Contracts;
using MoxSynth.Prices;
using MoxSynth.Profiles;

namespace MoxSynth.Api;

/// <summary>
/// JSON API for the Android app (and anything else that isn't a browser), under /api.
/// Sign-in returns a bearer access token (1 hour) + refresh token (30 days), both encrypted with the same Data
/// Protection keys as the web cookie and carrying the profile's password stamp: changing a password ends API
/// sessions too. Every authenticated request sets the request-scoped <see cref="CurrentProfile"/>, so the existing
/// repositories (per-profile collection database) work unchanged.
/// </summary>
public static class ApiEndpoints
{
    public static void AddMoxSynthApi(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddBearerToken(BearerTokenDefaults.AuthenticationScheme, options =>
            {
                options.BearerTokenExpiration = TimeSpan.FromHours(1);
                options.RefreshTokenExpiration = TimeSpan.FromDays(30);
            });
    }

    public static void MapMoxSynthApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // ------------------------------------------------------------ anonymous

        // Profile picker (names and colours only – same as the web sign-in screen).
        api.MapGet("/profiles", (ProfileStore store) => store.GetAll().Select(ApiMapper.ToDto));

        api.MapPost("/auth/login", async (LoginRequest request, ProfileStore store) =>
        {
            switch (store.VerifyPassword(request.ProfileId, request.Password))
            {
                case LoginResult.Ok:
                    return SignIn(store.Get(request.ProfileId)!);
                case LoginResult.NoPassword:
                    return Error(StatusCodes.Status403Forbidden, ApiErrorCodes.NoPassword,
                        "This profile has no password yet – set one in the web app first.");
                case LoginResult.LockedOut:
                    return Error(StatusCodes.Status429TooManyRequests, ApiErrorCodes.LockedOut,
                        ProfileErrors.Message(ProfileErrors.LockedOut) ?? "Too many attempts.");
                case LoginResult.NotFound:
                    return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Profile not found.");
                default:
                    await Task.Delay(400); // slow down guessing a little
                    return Error(StatusCodes.Status401Unauthorized, ApiErrorCodes.WrongPassword,
                        ProfileErrors.Message(ProfileErrors.WrongPassword) ?? "Wrong password.");
            }
        });

        api.MapPost("/auth/refresh", (RefreshRequest request, ProfileStore store,
            IOptionsMonitor<BearerTokenOptions> bearerOptions, TimeProvider time) =>
        {
            var ticket = bearerOptions.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector
                .Unprotect(request.RefreshToken);
            if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires)
                return Unauthorized();

            var profile = ProfileEndpoints.GetProfile(ticket.Principal, store);
            return profile is null ? Unauthorized() : SignIn(profile);
        });

        // ------------------------------------------------------------ signed in

        var auth = api.MapGroup("").AddEndpointFilter(RequireProfileAsync);

        auth.MapGet("/me", async (CurrentProfile current, CardRepository cards, PriceRepository prices,
            IOptions<PriceOptions> priceOptions) =>
        {
            var providers = priceOptions.Value.EffectiveProviders;
            var info = prices.GetInfo();
            return new MeDto(
                ApiMapper.ToDto(current.Value!),
                providers.Select(p => new PriceProviderDto(p, PriceProviders.Label(p), ApiMapper.CurrencyOf(p))).ToList(),
                providers.Contains(priceOptions.Value.DefaultProvider) ? priceOptions.Value.DefaultProvider : providers[0],
                (await cards.GetMetaAsync())?.Date,
                info?.SourceDate);
        });

        auth.MapGet("/sets", async (CardRepository cards, CancellationToken ct) =>
            (await cards.GetSetsAsync(ct)).Select(s => new SetDto(s.Code, s.Name, s.ReleaseDate, s.KeyruneCode)));

        // Same filters as the Search page: q = name words + optional collector number, sf = Scryfall syntax.
        auth.MapGet("/cards/search", async (string? q, string? sf, string? set, string? type, string? text, string? rarity,
            string? colors, bool? all, int? page, int? pageSize, string? provider,
            CardRepository cards, CollectionRepository collection, PriceRepository prices, CancellationToken ct) =>
        {
            var query = new CardSearchQuery
            {
                Name = q, Scryfall = sf, SetCode = set, Type = type, Text = text, Rarity = rarity, Colors = colors,
                IncludeNonPaper = all == true,
                Page = Math.Max(1, page ?? 1),
                PageSize = Math.Clamp(pageSize ?? 30, 1, 100),
            };
            if (!query.HasCriteria) return Results.Ok(new PageDto<CardSummaryDto>([], 0, query.Page, query.PageSize));

            var result = await cards.SearchAsync(query, ct);
            return Results.Ok(await ApiMapper.ToDtoPageAsync(result, provider, collection, prices, ct));
        });

        auth.MapGet("/cards/{uuid}", async (string uuid, string? provider, CardRepository cards,
            CollectionRepository collection, PriceRepository prices, CancellationToken ct) =>
        {
            var details = await cards.GetDetailsAsync(uuid, ct);
            if (details is null) return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Card not found.");

            var allPrices = await prices.GetAllForCardAsync(uuid, ct);
            var copies = await collection.GetCopiesAsync(uuid, includeRemoved: true, ct);
            var printings = await ApiMapper.ToDtosAsync(details.OtherPrintings, null, collection, prices, ct);
            return Results.Ok(ApiMapper.ToDto(details, allPrices, provider, copies, printings));
        });

        auth.MapPost("/cards/recognize", async (RecognizeRequest request, CardRecognizer recognizer, CardRepository cards,
            CollectionRepository collection, PriceRepository prices, string? provider, CancellationToken ct) =>
        {
            CardRecognition? result;
            string title, bottom;
            if (request.Lines is { Count: > 0 })
            {
                (result, title, bottom) = recognizer.RecognizeLines(request.Lines
                    .Select(l => new OcrTextLine(l.Text, l.X, l.Y, l.Width, l.Height)).ToList());
            }
            else
            {
                (title, bottom) = (request.Title ?? "", request.Bottom ?? "");
                result = recognizer.Recognize(title, bottom);
            }

            if (result is null) return new RecognizeResultDto(false, null, null, null, 0, [], title, bottom);

            // Same lookup as the web scanner: exact name, plus set / collector number when they were read.
            var matches = await cards.SearchAsync(new CardSearchQuery
            {
                Scryfall = $"!\"{result.Name.Replace("\"", "")}\"",
                SetCode = result.SetCode,
                Name = result.Number,
                PageSize = 30,
            }, ct);
            var candidates = await ApiMapper.ToDtosAsync(matches.Items, provider, collection, prices, ct);
            return new RecognizeResultDto(true, result.Name, result.SetCode, result.Number, result.Confidence,
                candidates, title, bottom);
        });

        // Collection groups, same filters as the Collection page.
        auth.MapGet("/collection", async (string? q, string? sf, string? set, string? finish, string? sort, DateTime? asOf,
            int? page, int? pageSize, string? provider, CollectionRepository collection, CardRepository cards,
            PriceRepository prices, CancellationToken ct) =>
        {
            var query = new CollectionQuery
            {
                Name = q, Scryfall = sf, SetCode = set, Finish = finish,
                Sort = Enum.TryParse<CollectionSort>(sort, true, out var s) ? s : CollectionSort.Name,
                AsOf = asOf?.ToUniversalTime(),
                PriceProvider = ApiMapper.ValidProvider(provider, prices),
                Page = Math.Max(1, page ?? 1),
                PageSize = Math.Clamp(pageSize ?? 50, 1, 200),
            };
            var result = await collection.GetGroupsAsync(query, ct);
            return await ApiMapper.ToDtoAsync(result, query.PriceProvider, cards, prices, ct);
        });

        auth.MapGet("/collection/sets", (CollectionRepository collection, CancellationToken ct) =>
            collection.GetSetCodesAsync(ct));

        auth.MapPost("/collection", async (AddCardsDto request, CardRepository cards, CollectionRepository collection,
            CancellationToken ct) =>
        {
            if (request.Quantity is < 1 or > 1000)
                return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Invalid, "Quantity must be between 1 and 1000.");
            if (!Enum.TryParse<CardCondition>(request.Condition, out var condition))
                return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Invalid, "Unknown condition.");

            var card = (await cards.GetSummariesAsync([request.CardUuid], ct)).GetValueOrDefault(request.CardUuid);
            if (card is null) return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Card not found.");
            if (!card.FinishList.Contains(request.Finish))
                return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Invalid, $"This printing has no {request.Finish} finish.");

            var ids = await collection.AddAsync(new AddCardsRequest
            {
                CardUuid = card.Uuid,
                Name = card.Name,
                SetCode = card.SetCode,
                Number = card.Number,
                Finish = request.Finish,
                Condition = condition,
                Language = string.IsNullOrWhiteSpace(request.Language) ? card.Language ?? "English" : request.Language.Trim(),
                PurchasePrice = request.PurchasePrice,
                Notes = request.Notes,
                Quantity = request.Quantity,
            }, ct);
            var owned = (await collection.GetOwnedCountsAsync([card.Uuid], ct)).GetValueOrDefault(card.Uuid);
            return Results.Ok(new AddCardsResultDto(ids, owned));
        });

        auth.MapPost("/collection/remove-one", async (RemoveOneDto request, CollectionRepository collection, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CardCondition>(request.Condition, out var condition))
                return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Invalid, "Unknown condition.");
            var removed = await collection.RemoveOneAsync(request.CardUuid, request.Finish, condition, request.Language,
                request.Reason, ct);
            return removed ? Results.NoContent() : Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "No owned copy left.");
        });

        auth.MapDelete("/collection/{id:long}", async (long id, string? reason, CollectionRepository collection, CancellationToken ct) =>
            await collection.RemoveAsync(id, reason, ct: ct)
                ? Results.NoContent()
                : Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Copy not found or already removed."));

        auth.MapPost("/collection/{id:long}/restore", async (long id, CollectionRepository collection, CancellationToken ct) =>
            await collection.RestoreAsync(id, ct)
                ? Results.NoContent()
                : Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Copy not found or not removed."));

        auth.MapGet("/stats", async (string? provider, CollectionRepository collection, PriceRepository prices, CancellationToken ct) =>
        {
            var stats = await collection.GetStatsAsync(ct);
            var p = ApiMapper.ValidProvider(provider, prices);
            var value = p is null ? null : await collection.GetValueAsync(p, ct);
            return new StatsDto(stats.OwnedCopies, stats.UniquePrintings, stats.UniqueNames, stats.RemovedCopies,
                stats.AddedLast30Days, stats.RemovedLast30Days, value?.TotalValue, value?.Currency, value?.UnpricedCopies ?? 0);
        });

        auth.MapGet("/history", async (int? page, int? pageSize, CollectionRepository collection, CancellationToken ct) =>
        {
            var result = await collection.GetHistoryAsync(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 200), ct);
            return new PageDto<HistoryEventDto>(result.Items.Select(ApiMapper.ToDto).ToList(), result.TotalCount,
                result.Page, result.PageSize);
        });
    }

    /// <summary>Authenticates the bearer token and makes its profile the current one for this request.</summary>
    private static async ValueTask<object?> RequireProfileAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var result = await http.AuthenticateAsync(BearerTokenDefaults.AuthenticationScheme);
        var profile = result.Succeeded
            ? ProfileEndpoints.GetProfile(result.Principal, http.RequestServices.GetRequiredService<ProfileStore>())
            : null;
        if (profile is null) return Unauthorized();

        http.User = result.Principal!;
        http.RequestServices.GetRequiredService<CurrentProfile>().Set(profile);
        return await next(context);
    }

    private static IResult SignIn(Profile profile) =>
        Results.SignIn(ProfileEndpoints.CreatePrincipal(profile, BearerTokenDefaults.AuthenticationScheme),
            authenticationScheme: BearerTokenDefaults.AuthenticationScheme);

    private static IResult Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "Please sign in again.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: status);
}
