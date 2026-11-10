using MoxSynth.Contracts;
using MoxSynth.Decks;
using MoxSynth.Prices;

namespace MoxSynth.Api;

/// <summary>
/// Deck endpoints under /api/decks (mapped inside the signed-in group of <see cref="ApiEndpoints"/>). Mutations answer
/// with the updated <see cref="DeckDetailDto"/> (prices at <c>?provider=</c>), so the app refreshes in one round trip.
/// Card and category routes carry the deck id; a card or category of another deck is "not found".
/// Errors: <see cref="DeckNotFoundException"/> 404, <see cref="ArgumentException"/> 400, <see cref="InvalidOperationException"/> 409.
/// </summary>
internal static class DeckApiEndpoints
{
    public static void MapDeckApi(this IEndpointRouteBuilder auth)
    {
        var decks = auth.MapGroup("/decks").AddEndpointFilter(MapDeckErrorsAsync);

        decks.MapGet("/", async (string? provider, DeckRepository repo, PriceRepository prices, CancellationToken ct) =>
            (await repo.ListAsync(ApiMapper.ValidProvider(provider, prices), ct)).Select(ApiMapper.ToDto).ToList());

        decks.MapGet("/formats", async (DeckRepository repo, CancellationToken ct) =>
            (await repo.GetFormatsAsync(ct)).Select(ApiMapper.ToFormatDto).ToList());

        decks.MapPost("/", async (CreateDeckRequest request, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            var id = await repo.CreateAsync(request.Name, request.Format, request.Description, ct);
            return Results.Created($"/api/decks/{id}", await DetailAsync(repo, prices, id, provider, ct));
        });

        decks.MapGet("/{id:long}", (long id, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) => DetailAsync(repo, prices, id, provider, ct));

        decks.MapPut("/{id:long}", async (long id, UpdateDeckRequest request, string? provider, DeckRepository repo,
            PriceRepository prices, CancellationToken ct) =>
        {
            await repo.UpdateAsync(id, request.Name, request.Format, request.Description, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapDelete("/{id:long}", async (long id, DeckRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        decks.MapPost("/{id:long}/duplicate", async (long id, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            var copyId = await repo.DuplicateAsync(id, ct);
            return Results.Created($"/api/decks/{copyId}", await DetailAsync(repo, prices, copyId, provider, ct));
        });

        // ------------------------------------------------------------ cards

        decks.MapPost("/{id:long}/cards", async (long id, AddDeckCardRequest request, string? provider, DeckRepository repo,
            PriceRepository prices, CancellationToken ct) =>
        {
            await repo.AddCardAsync(id, request.CardUuid, request.Quantity, request.Finish, request.Category, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapPut("/{id:long}/cards/{deckCardId:long}/quantity", async (long id, long deckCardId,
            SetDeckCardQuantityRequest request, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            await RequireCardAsync(repo, id, deckCardId, ct);
            await repo.SetQuantityAsync(deckCardId, request.Quantity, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapPut("/{id:long}/cards/{deckCardId:long}/categories", async (long id, long deckCardId,
            SetDeckCardCategoriesRequest request, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            await RequireCardAsync(repo, id, deckCardId, ct);
            await repo.SetCategoriesAsync(deckCardId, request.Categories ?? [], ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapPut("/{id:long}/cards/{deckCardId:long}/printing", async (long id, long deckCardId,
            ChangeDeckCardPrintingRequest request, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            await RequireCardAsync(repo, id, deckCardId, ct);
            await repo.ChangePrintingAsync(deckCardId, request.CardUuid, request.Finish, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        // ------------------------------------------------------------ categories

        decks.MapPost("/{id:long}/categories", async (long id, CreateDeckCategoryRequest request, string? provider,
            DeckRepository repo, PriceRepository prices, CancellationToken ct) =>
        {
            await repo.AddCategoryAsync(id, request.Name, request.CountsInDeck, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapPut("/{id:long}/categories/{categoryId:long}", async (long id, long categoryId,
            UpdateDeckCategoryRequest request, string? provider, DeckRepository repo, PriceRepository prices,
            CancellationToken ct) =>
        {
            await RequireCategoryAsync(repo, id, categoryId, ct);
            if (request.Name is not null) await repo.RenameCategoryAsync(categoryId, request.Name, ct);
            if (request.CountsInDeck is { } counts) await repo.SetCategoryCountsAsync(categoryId, counts, ct);
            if (request.Position is { } position) await repo.MoveCategoryAsync(categoryId, position, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        decks.MapDelete("/{id:long}/categories/{categoryId:long}", async (long id, long categoryId, string? provider,
            DeckRepository repo, PriceRepository prices, CancellationToken ct) =>
        {
            await RequireCategoryAsync(repo, id, categoryId, ct);
            await repo.DeleteCategoryAsync(categoryId, ct);
            return await DetailAsync(repo, prices, id, provider, ct);
        });

        // ------------------------------------------------------------ text

        decks.MapPost("/{id:long}/import", async (long id, ImportDeckTextRequest request, string? provider,
            DeckRepository repo, PriceRepository prices, CancellationToken ct) =>
        {
            var result = await repo.ImportTextAsync(id, request.Text ?? "", request.Replace, ct);
            return ApiMapper.ToDto(result, await repo.GetAsync(id, ApiMapper.ValidProvider(provider, prices), ct));
        });

        decks.MapGet("/{id:long}/export", async (long id, DeckRepository repo, CancellationToken ct) =>
            new DeckTextDto(await repo.ExportTextAsync(id, ct)));
    }

    private static async Task<DeckDetailDto> DetailAsync(DeckRepository repo, PriceRepository prices, long deckId,
        string? provider, CancellationToken ct) =>
        ApiMapper.ToDto(await repo.GetAsync(deckId, ApiMapper.ValidProvider(provider, prices), ct));

    /// <summary>Makes sure the deck card belongs to the deck in the route (the repository works by card id alone).</summary>
    private static async Task RequireCardAsync(DeckRepository repo, long deckId, long deckCardId, CancellationToken ct)
    {
        var deck = await repo.GetAsync(deckId, null, ct);
        if (!deck.Cards.Any(c => c.DeckCardId == deckCardId))
            throw new DeckNotFoundException("Card not found in this deck.");
    }

    /// <summary>Makes sure the category belongs to the deck in the route.</summary>
    private static async Task RequireCategoryAsync(DeckRepository repo, long deckId, long categoryId, CancellationToken ct)
    {
        var deck = await repo.GetAsync(deckId, null, ct);
        if (!deck.Categories.Any(c => c.Id == categoryId))
            throw new DeckNotFoundException("Category not found in this deck.");
    }

    /// <summary>Repository exceptions → the API error body.</summary>
    private static async ValueTask<object?> MapDeckErrorsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Invalid,
                ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", ""));
        }
        catch (InvalidOperationException ex)
        {
            return Error(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, ex.Message);
        }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: status);
}
