using System.IO.Compression;

namespace MoxSynth.Infrastructure;

/// <summary>
/// Makes sure AllPrintings.sqlite exists. If only the MTGJSON zip download is present,
/// the sqlite file is extracted next to it on first start.
/// </summary>
public static class CardDatabaseBootstrapper
{
    public static void EnsureAvailable(DatabasePaths paths, ILogger logger)
    {
        if (File.Exists(paths.AllPrintingsPath))
        {
            return;
        }

        if (paths.AllPrintingsZipPath is null || !File.Exists(paths.AllPrintingsZipPath))
        {
            logger.LogWarning(
                "Card database not found at {Path}. Download AllPrintings.sqlite(.zip) from mtgjson.com and place it there.",
                paths.AllPrintingsPath);
            return;
        }

        logger.LogInformation("Extracting {Zip} to {Path}…", paths.AllPrintingsZipPath, paths.AllPrintingsPath);

        using var archive = ZipFile.OpenRead(paths.AllPrintingsZipPath);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            logger.LogError("No .sqlite file found inside {Zip}.", paths.AllPrintingsZipPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(paths.AllPrintingsPath)!);
        var tempPath = paths.AllPrintingsPath + ".extracting";
        entry.ExtractToFile(tempPath, overwrite: true);
        File.Move(tempPath, paths.AllPrintingsPath, overwrite: true);

        logger.LogInformation("Card database extracted ({Size:N0} bytes).", new FileInfo(paths.AllPrintingsPath).Length);
    }
}
