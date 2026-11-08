using Microsoft.Data.Sqlite;

namespace MoxSynth.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Databases";

    /// <summary>Path to MTGJSON AllPrintings.sqlite (read-only card catalogue).</summary>
    public string AllPrintings { get; set; } = "App_Data/AllPrintings.sqlite";

    /// <summary>Optional zip that is extracted to <see cref="AllPrintings"/> when the sqlite file is missing.</summary>
    public string? AllPrintingsZip { get; set; } = "App_Data/AllPrintings.sqlite.zip";

    /// <summary>Profiles database (who uses MoxSynth).</summary>
    public string Profiles { get; set; } = "App_Data/profiles.db";

    /// <summary>Folder holding one collection database per profile.</summary>
    public string CollectionsFolder { get; set; } = "App_Data/collections";

    /// <summary>
    /// Single-user collection database from before profiles existed. If present on first start it becomes the
    /// collection of the first (admin) profile.
    /// </summary>
    public string Collection { get; set; } = "App_Data/collection.db";
}

public sealed record DatabasePaths(
    string AllPrintingsPath,
    string? AllPrintingsZipPath,
    string ProfilesPath,
    string CollectionsFolder,
    string LegacyCollectionPath)
{
    public string AllPrintingsConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = AllPrintingsPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();

    public static DatabasePaths Resolve(DatabaseOptions options, string contentRoot)
    {
        static string Full(string path, string root) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));

        return new DatabasePaths(
            Full(options.AllPrintings, contentRoot),
            string.IsNullOrWhiteSpace(options.AllPrintingsZip) ? null : Full(options.AllPrintingsZip, contentRoot),
            Full(options.Profiles, contentRoot),
            Full(options.CollectionsFolder, contentRoot),
            Full(options.Collection, contentRoot));
    }
}
