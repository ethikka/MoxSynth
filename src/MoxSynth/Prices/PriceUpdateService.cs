using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MoxSynth.Infrastructure;

namespace MoxSynth.Prices;

/// <summary>
/// Keeps prices.db up to date in the background:
/// - on start-up, (re)imports when App_Data/AllPrices.json.zip is newer than the current prices.db;
/// - on request (Prices page): import the zip, an uploaded file, or download the latest file from MTGJSON first.
/// Only one import runs at a time; the app stays usable (old prices are served until the new database is swapped in).
/// </summary>
public sealed class PriceUpdateService(
    DatabasePaths paths,
    PriceRepository repository,
    IOptions<PriceOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<PriceUpdateService> logger) : BackgroundService
{
    private enum Job { ImportZip, DownloadAndImport }

    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object _lock = new();
    private bool _running;
    private string _message = "";
    private long _cards;
    private string? _error;

    /// <summary>Raised whenever the status changes (progress, finished, failed).</summary>
    public event Action? StatusChanged;

    public PriceStatus Status
    {
        get
        {
            var info = repository.GetInfo();
            lock (_lock)
                return new PriceStatus(_running, _message, _cards, info?.ImportedAtUtc, info?.SourceDate, info?.Rows ?? 0, _error);
        }
    }

    public bool ZipAvailable => File.Exists(paths.AllPricesZipPath);
    public string ZipPath => paths.AllPricesZipPath;
    public DateTime? ZipModifiedUtc => ZipAvailable ? File.GetLastWriteTimeUtc(paths.AllPricesZipPath) : null;

    public bool RequestImport() => _jobs.Writer.TryWrite(Job.ImportZip);
    public bool RequestDownload() => _jobs.Writer.TryWrite(Job.DownloadAndImport);

    /// <summary>Saves an uploaded AllPrices.json.zip to App_Data and queues the import.</summary>
    public async Task SaveUploadAsync(Stream upload, CancellationToken ct)
    {
        var temp = paths.AllPricesZipPath + ".upload";
        await using (var file = File.Create(temp))
            await upload.CopyToAsync(file, ct);
        File.Move(temp, paths.AllPricesZipPath, overwrite: true);
        RequestImport();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start-up: import if the zip is newer than the database (or there is no database yet).
        if (ZipAvailable && (!repository.IsAvailable ||
                             File.GetLastWriteTimeUtc(paths.AllPricesZipPath) > File.GetLastWriteTimeUtc(paths.PricesPath)))
        {
            RequestImport();
        }

        await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                SetStatus(running: true, "Starting…", 0, error: null);
                if (job == Job.DownloadAndImport) await DownloadAsync(stoppingToken);

                SetStatus(true, "Importing prices…", 0, null);
                var providers = options.Value.EffectiveProviders;
                var started = DateTime.UtcNow;
                var result = await Task.Run(() => PriceImporter.Import(paths.AllPricesZipPath, paths.PricesPath, providers,
                    cards => SetStatus(true, "Importing prices…", cards, null), stoppingToken), stoppingToken);

                logger.LogInformation("Prices imported from {Date}: {Cards:N0} cards, {Rows:N0} prices in {Seconds:N0} s.",
                    result.SourceDate, result.Cards, result.Rows, (DateTime.UtcNow - started).TotalSeconds);
                SetStatus(false, $"Imported prices of {result.SourceDate}: {result.Rows:N0} prices for {result.Cards:N0} cards.", result.Cards, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Price import failed.");
                SetStatus(false, "Import failed.", 0, ex.Message);
            }
        }
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        var url = options.Value.DownloadUrl;
        SetStatus(true, $"Downloading {url}…", 0, null);
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var temp = paths.AllPricesZipPath + ".download";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(temp))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
                {
                    lastReport = DateTime.UtcNow;
                    var pct = total is > 0 ? $" ({done * 100 / total:N0}%)" : "";
                    SetStatus(true, $"Downloading… {done / (1024 * 1024):N0} MB{pct}", 0, null);
                }
            }
        }
        File.Move(temp, paths.AllPricesZipPath, overwrite: true);
    }

    private void SetStatus(bool running, string message, long cards, string? error)
    {
        lock (_lock)
        {
            _running = running;
            _message = message;
            _cards = cards;
            _error = error;
        }
        StatusChanged?.Invoke();
    }
}

/// <summary>Price provider shown in this browser tab (navbar selector; remembered in localStorage).</summary>
public sealed class PriceSettings(IOptions<PriceOptions> options)
{
    public IReadOnlyList<string> Providers => options.Value.EffectiveProviders;

    public string Provider { get; private set; } =
        options.Value.EffectiveProviders.Contains(options.Value.DefaultProvider)
            ? options.Value.DefaultProvider
            : options.Value.EffectiveProviders[0];

    public event Action? Changed;

    public void Set(string provider)
    {
        if (!Providers.Contains(provider) || provider == Provider) return;
        Provider = provider;
        Changed?.Invoke();
    }
}
