using MoxSynth.Cards;
using MoxSynth.Collection;
using MoxSynth.Contracts;
using MoxSynth.Infrastructure;
using MoxSynth.Prices;
using MoxSynth.Profiles;

namespace MoxSynth.Api;

/// <summary>Domain models → API contracts.</summary>
internal static class ApiMapper
{
    private static readonly string[] KnownProviders = ["cardmarket", "tcgplayer", "cardkingdom", "manapool"];

    public static string CurrencyOf(string provider) => provider == "cardmarket" ? "EUR" : "USD";

    /// <summary>The provider when prices exist and it is a known provider, else null (no prices).</summary>
    public static string? ValidProvider(string? provider, PriceRepository prices) =>
        provider is not null && KnownProviders.Contains(provider) && prices.IsAvailable ? provider : null;

    public static ProfileDto ToDto(Profile p) => new(p.Id, p.Name, p.Color, p.Initial, p.IsAdmin, p.HasPassword);

    public static PriceDto ToDto(PriceInfo p, bool withHistory, bool withStats)
    {
        IReadOnlyList<double?>? history = null;
        if (withHistory)
            history = p.HistoryValues().Select(v => float.IsNaN(v) ? (double?)null : Math.Round(v, 2)).ToList();

        PriceStatsDto? stats = null;
        if (withStats)
        {
            var s = p.Stats();
            stats = new PriceStatsDto(s.Low, s.LowDate, s.High, s.HighDate, Math.Round(s.Average, 2), s.DaysWithPrice);
        }

        return new PriceDto(p.Provider, p.Finish, p.Currency, p.Price, p.Change7d, p.Change30d, p.ChangeFirst,
            p.FirstDate, p.LastDate, p.IsStale, history, stats);
    }

    public static async Task<PageDto<CardSummaryDto>> ToDtoPageAsync(PagedResult<CardSummary> page, string? provider,
        CollectionRepository collection, PriceRepository prices, CancellationToken ct) =>
        new(await ToDtosAsync(page.Items, provider, collection, prices, ct), page.TotalCount, page.Page, page.PageSize);

    /// <summary>Card summaries with owned counts and (when a provider is given) prices per finish.</summary>
    public static async Task<IReadOnlyList<CardSummaryDto>> ToDtosAsync(IReadOnlyList<CardSummary> cards, string? provider,
        CollectionRepository collection, PriceRepository prices, CancellationToken ct)
    {
        var uuids = cards.Select(c => c.Uuid).ToList();
        var owned = await collection.GetOwnedCountsAsync(uuids, ct);
        var priceMap = ValidProvider(provider, prices) is { } p
            ? await prices.GetAsync(uuids, p, ct)
            : new Dictionary<string, Dictionary<string, PriceInfo>>();

        return cards.Select(c =>
        {
            var finishes = c.FinishList;
            var cardPrices = priceMap.GetValueOrDefault(c.Uuid) is { } byFinish
                ? finishes.Where(byFinish.ContainsKey).Select(f => ToDto(byFinish[f], withHistory: true, withStats: false)).ToList()
                : [];
            return ToDto(c, owned.GetValueOrDefault(c.Uuid), cardPrices);
        }).ToList();
    }

    public static CardSummaryDto ToDto(CardSummary c, long owned, IReadOnlyList<PriceDto> prices) => new(
        c.Uuid, c.Name, c.SetCode, c.SetName, c.Number, c.Rarity, c.ManaCost, c.Type, c.FinishList, c.Language,
        ScryfallImages.Url(c.ScryfallId), ScryfallImages.Url(c.ScryfallId, "small"), owned, prices);

    public static CardDetailDto ToDto(CardDetails d, IReadOnlyList<PriceInfo> prices, string? provider,
        IReadOnlyList<CollectionCard> copies, IReadOnlyList<CardSummaryDto> otherPrintings)
    {
        var p = d.Primary;
        // Requested provider first, then the others; within a provider in the card's finish order.
        var finishOrder = d.FinishList.ToList();
        var orderedPrices = prices
            .OrderBy(x => x.Provider == provider ? 0 : 1)
            .ThenBy(x => x.Provider)
            .ThenBy(x => finishOrder.IndexOf(x.Finish) is var i && i < 0 ? 99 : i)
            .Select(x => ToDto(x, withHistory: true, withStats: true))
            .ToList();

        return new CardDetailDto(
            d.Uuid, d.Name, p.SetCode, p.SetName, p.Number, p.Rarity, p.Language, p.ReleaseDate, d.FinishList,
            ScryfallImages.Url(p.ScryfallId, "normal"),
            d.HasBackImage ? ScryfallImages.Url(p.ScryfallId, "normal", back: true) : null,
            d.Faces.Select(f => new CardFaceDto(f.DisplayName, f.ManaCost, f.Type, f.Text, f.FlavorText, f.Power,
                f.Toughness, f.Loyalty, f.Defense, f.Artist)).ToList(),
            otherPrintings,
            orderedPrices,
            copies.Select(ToDto).ToList());
    }

    public static CopyDto ToDto(CollectionCard c) => new(c.Id, c.Finish, c.Condition.ToString(), c.Language,
        c.PurchasePrice, c.Notes, c.AddedAt, c.RemovedAt, c.RemovalReason);

    public static HistoryEventDto ToDto(HistoryEvent e) => new(e.Id, e.CardUuid, e.Name, e.SetCode, e.Number, e.Finish,
        e.Condition.ToString(), e.Kind, e.RemovalReason, e.At);

    public static async Task<CollectionPageDto> ToDtoAsync(CollectionPage page, string? provider, CardRepository cards,
        PriceRepository prices, CancellationToken ct)
    {
        var groups = page.Groups.Items;
        var uuids = groups.Select(g => g.CardUuid).Distinct().ToList();
        var summaries = await cards.GetSummariesAsync(uuids, ct);
        var priceMap = provider is not null
            ? await prices.GetAsync(uuids, provider, ct)
            : new Dictionary<string, Dictionary<string, PriceInfo>>();

        var items = groups.Select(g =>
        {
            var summary = summaries.GetValueOrDefault(g.CardUuid);
            var price = priceMap.GetValueOrDefault(g.CardUuid)?.GetValueOrDefault(g.Finish);
            return new CollectionGroupDto(g.CardUuid, g.Name, g.SetCode, g.Number, g.Finish, g.Condition.ToString(),
                g.Language, g.Quantity, g.FirstAddedAt, g.LastAddedAt, summary?.ManaCost,
                ScryfallImages.Url(summary?.ScryfallId, "small"),
                price is null ? null : ToDto(price, withHistory: true, withStats: false),
                price is null ? null : Math.Round(price.Price * g.Quantity, 2));
        }).ToList();

        return new CollectionPageDto(
            new PageDto<CollectionGroupDto>(items, page.Groups.TotalCount, page.Groups.Page, page.Groups.PageSize),
            page.TotalCopies, page.TotalValue, page.Currency, page.UnpricedCopies);
    }
}
