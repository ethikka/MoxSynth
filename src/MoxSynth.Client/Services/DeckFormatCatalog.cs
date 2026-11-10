using MoxSynth.Contracts;

namespace MoxSynth.Client.Services;

/// <summary>The server's deck formats (labels, deck sizes), fetched once and shared by the deck pages.</summary>
public sealed class DeckFormatCatalog
{
    private readonly MoxApiClient api;
    private Task<List<DeckFormatDto>>? _formats;

    public DeckFormatCatalog(MoxApiClient api, AppSession session)
    {
        this.api = api;
        // The next account may be on another server.
        session.SignedOut += () => _formats = null;
    }

    public async Task<IReadOnlyList<DeckFormatDto>> GetAsync()
    {
        var task = _formats ??= api.GetDeckFormatsAsync();
        try
        {
            return await task;
        }
        catch
        {
            if (_formats == task) _formats = null; // try again next time
            throw;
        }
    }

    /// <summary>"Commander" for "commander"; the id itself (capitalised) when the server doesn't know it.</summary>
    public static string Label(IReadOnlyList<DeckFormatDto> formats, string id) =>
        formats.FirstOrDefault(f => f.Id == id)?.Label ??
        (string.IsNullOrEmpty(id) ? "" : char.ToUpperInvariant(id[0]) + id[1..]);

    /// <summary>Target deck size: the commander deck size of a commander-style format, else 60.</summary>
    public static int DeckSize(IReadOnlyList<DeckFormatDto> formats, string id) =>
        formats.FirstOrDefault(f => f.Id == id)?.CommanderDeckSize ?? 60;
}
