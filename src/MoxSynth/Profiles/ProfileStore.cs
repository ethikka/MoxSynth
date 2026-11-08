using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using MoxSynth.Collection;
using MoxSynth.Infrastructure;

namespace MoxSynth.Profiles;

public enum LoginResult
{
    Ok,
    WrongPassword,
    LockedOut,
    NoPassword,
    NotFound,
}

/// <summary>
/// Stores profiles in App_Data/profiles.db and owns the per-profile collection databases
/// (App_Data/collections/collection-{id}.db). Every profile is protected by a password.
/// </summary>
public sealed class ProfileStore(DatabasePaths paths, ILogger<ProfileStore> logger)
{
    private const string Columns = "id, name, color, is_admin, db_file, created_at, password_hash, password_stamp";
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    // Failed sign-in attempts per profile (in memory; resets on restart, which is fine for brute-force throttling).
    private readonly ConcurrentDictionary<long, (int Failures, DateTime LockedUntil)> _failures = new();

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = paths.ProfilesPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    private SqliteConnection Open() => new(ConnectionString);

    /// <summary>Creates/upgrades profiles.db, adopts a pre-profiles collection.db as the first (admin) profile, migrates all collections.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ProfilesPath)!);
        Directory.CreateDirectory(paths.CollectionsFolder);

        using (var conn = Open())
        {
            conn.Execute("""
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS profiles (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    name       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                    color      TEXT    NOT NULL,
                    is_admin   INTEGER NOT NULL DEFAULT 0,
                    db_file    TEXT    NOT NULL,
                    created_at TEXT    NOT NULL
                );
                """);

            // Password columns were added later – upgrade existing profiles.db files.
            var columns = conn.Query<string>("SELECT name FROM pragma_table_info('profiles')").ToHashSet();
            if (!columns.Contains("password_hash")) conn.Execute("ALTER TABLE profiles ADD COLUMN password_hash TEXT NULL");
            if (!columns.Contains("password_stamp")) conn.Execute("ALTER TABLE profiles ADD COLUMN password_stamp TEXT NULL");

            var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles");
            if (count == 0 && File.Exists(paths.LegacyCollectionPath))
            {
                // Collection from before profiles existed → becomes the admin's collection, file stays where it is.
                // It has no password yet: the first person to pick it is asked to set one.
                conn.Execute("""
                    INSERT INTO profiles (name, color, is_admin, db_file, created_at)
                    VALUES ('Admin', @color, 1, @dbFile, @now)
                    """, new { color = ProfileColors.All[3], dbFile = paths.LegacyCollectionPath, now = SqliteTime.ToDb(DateTime.UtcNow) });
                logger.LogInformation("Existing collection {Path} assigned to new admin profile 'Admin'.", paths.LegacyCollectionPath);
            }
        }

        foreach (var profile in GetAll())
            CollectionSchema.Migrate(DatabasePathOf(profile), logger);
    }

    public IReadOnlyList<Profile> GetAll()
    {
        using var conn = Open();
        return conn.Query<Profile>($"SELECT {Columns} FROM profiles ORDER BY name COLLATE NOCASE").AsList();
    }

    public Profile? Get(long id)
    {
        using var conn = Open();
        return conn.QuerySingleOrDefault<Profile>($"SELECT {Columns} FROM profiles WHERE id = @id", new { id });
    }

    public bool Any()
    {
        using var conn = Open();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles") > 0;
    }

    /// <summary>Full path of a profile's collection database.</summary>
    public string DatabasePathOf(Profile profile) =>
        Path.IsPathRooted(profile.DbFile) ? profile.DbFile : Path.Combine(paths.CollectionsFolder, profile.DbFile);

    public string ConnectionStringOf(Profile profile) => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePathOf(profile),
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    /// <summary>Number of cards currently in a profile's collection (for the admin overview).</summary>
    public long CountOwnedCards(Profile profile)
    {
        if (!File.Exists(DatabasePathOf(profile))) return 0;
        using var conn = new SqliteConnection(ConnectionStringOf(profile));
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM collection_cards WHERE removed_at IS NULL");
    }

    // ------------------------------------------------------------------ passwords

    /// <summary>Checks a password, with a one-minute lockout after <see cref="MaxFailedAttempts"/> wrong attempts.</summary>
    public LoginResult VerifyPassword(long id, string? password)
    {
        var profile = Get(id);
        if (profile is null) return LoginResult.NotFound;
        if (!profile.HasPassword) return LoginResult.NoPassword;

        _failures.TryGetValue(id, out var state);
        if (state.LockedUntil > DateTime.UtcNow) return LoginResult.LockedOut;

        if (PasswordHashing.Verify(password ?? "", profile.PasswordHash))
        {
            _failures.TryRemove(id, out _);
            return LoginResult.Ok;
        }

        var failures = state.Failures + 1;
        if (failures >= MaxFailedAttempts)
        {
            _failures[id] = (0, DateTime.UtcNow + LockoutDuration);
            logger.LogWarning("Profile {Name} (#{Id}) locked for a minute after {Count} wrong passwords.", profile.Name, id, failures);
            return LoginResult.LockedOut;
        }

        _failures[id] = (failures, DateTime.MinValue);
        logger.LogWarning("Wrong password for profile {Name} (#{Id}).", profile.Name, id);
        return LoginResult.WrongPassword;
    }

    /// <summary>Sets a new password; existing sign-ins of this profile stop working (new stamp).</summary>
    public Profile SetPassword(long id, string? password, string? confirmation)
    {
        ValidatePassword(password, confirmation);
        using var conn = Open();
        var rows = conn.Execute("UPDATE profiles SET password_hash = @hash, password_stamp = @stamp WHERE id = @id",
            new { id, hash = PasswordHashing.Hash(password!), stamp = PasswordHashing.NewStamp() });
        if (rows == 0) throw new ProfileException(ProfileErrors.NotFound);
        _failures.TryRemove(id, out _);
        return Get(id)!;
    }

    public static void ValidatePassword(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password) || password.Length < PasswordHashing.MinimumLength)
            throw new ProfileException(ProfileErrors.PasswordTooShort);
        if (password != confirmation)
            throw new ProfileException(ProfileErrors.PasswordMismatch);
    }

    // ------------------------------------------------------------------ profiles

    /// <summary>Creates a password-protected profile with an empty collection. The very first profile is always admin.</summary>
    public Profile Create(string name, string color, bool isAdmin, string? password, string? confirmation)
    {
        name = ValidateName(name);
        ValidatePassword(password, confirmation);

        using var conn = Open();
        conn.Open();
        using var tx = conn.BeginTransaction();

        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles WHERE name = @name", new { name }, tx) > 0)
            throw new ProfileException(ProfileErrors.NameTaken);

        var first = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles", transaction: tx) == 0;
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO profiles (name, color, is_admin, db_file, created_at, password_hash, password_stamp)
            VALUES (@name, @color, @isAdmin, '', @now, @hash, @stamp);
            SELECT last_insert_rowid();
            """, new
        {
            name,
            color,
            isAdmin = isAdmin || first,
            now = SqliteTime.ToDb(DateTime.UtcNow),
            hash = PasswordHashing.Hash(password!),
            stamp = PasswordHashing.NewStamp(),
        }, tx);
        conn.Execute("UPDATE profiles SET db_file = @dbFile WHERE id = @id", new { id, dbFile = $"collection-{id}.db" }, tx);
        tx.Commit();

        var profile = Get(id)!;
        CollectionSchema.Migrate(DatabasePathOf(profile), logger);
        logger.LogInformation("Profile {Name} (#{Id}) created.", profile.Name, id);
        return profile;
    }

    public void Update(long id, string name, string color, bool isAdmin)
    {
        name = ValidateName(name);
        using var conn = Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles WHERE name = @name AND id <> @id", new { name, id }) > 0)
            throw new ProfileException(ProfileErrors.NameTaken);
        if (!isAdmin && IsLastAdmin(conn, id))
            throw new ProfileException(ProfileErrors.LastAdmin);

        conn.Execute("UPDATE profiles SET name = @name, color = @color, is_admin = @isAdmin WHERE id = @id",
            new { id, name, color, isAdmin });
    }

    /// <summary>
    /// Deletes a profile. Its collection database is not destroyed but moved to App_Data/collections/deleted/,
    /// so an accidental delete can be recovered by hand.
    /// </summary>
    public void Delete(long id)
    {
        var profile = Get(id) ?? throw new ProfileException(ProfileErrors.NotFound);
        using var conn = Open();
        if (IsLastAdmin(conn, id))
            throw new ProfileException(ProfileErrors.LastAdmin);

        conn.Execute("DELETE FROM profiles WHERE id = @id", new { id });
        SqliteConnection.ClearAllPools(); // release file handles before moving the database

        var dbPath = DatabasePathOf(profile);
        var deletedFolder = Path.Combine(paths.CollectionsFolder, "deleted");
        Directory.CreateDirectory(deletedFolder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = dbPath + suffix;
            if (File.Exists(file))
                File.Move(file, Path.Combine(deletedFolder, $"{Path.GetFileNameWithoutExtension(dbPath)}-{profile.Name}-{stamp}{Path.GetExtension(dbPath)}{suffix}"));
        }
        logger.LogInformation("Profile {Name} (#{Id}) deleted; collection moved to {Folder}.", profile.Name, id, deletedFolder);
    }

    private static bool IsLastAdmin(SqliteConnection conn, long id) =>
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles WHERE is_admin = 1 AND id <> @id", new { id }) == 0
        && conn.ExecuteScalar<long>("SELECT is_admin FROM profiles WHERE id = @id", new { id }) == 1;

    private static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ProfileException(ProfileErrors.NameEmpty);
        if (name.Length > 40) throw new ProfileException(ProfileErrors.NameTooLong);
        return name;
    }
}

/// <summary>Error codes (safe to pass in URLs) and their messages.</summary>
public static class ProfileErrors
{
    public const string NameEmpty = "name-empty";
    public const string NameTooLong = "name-long";
    public const string NameTaken = "name-taken";
    public const string LastAdmin = "last-admin";
    public const string NotFound = "not-found";
    public const string PasswordTooShort = "password-short";
    public const string PasswordMismatch = "password-mismatch";
    public const string WrongPassword = "wrong-password";
    public const string LockedOut = "locked";
    public const string NotAllowed = "not-allowed";

    public static string? Message(string? code) => code switch
    {
        NameEmpty => "Please enter a name.",
        NameTooLong => "Names can be at most 40 characters.",
        NameTaken => "A profile with that name already exists.",
        LastAdmin => "There must always be at least one admin.",
        NotFound => "Profile not found.",
        PasswordTooShort => $"Passwords need at least {PasswordHashing.MinimumLength} characters.",
        PasswordMismatch => "The passwords don't match.",
        WrongPassword => "Wrong password.",
        LockedOut => "Too many wrong attempts – try again in a minute.",
        NotAllowed => "That isn't allowed.",
        _ => null,
    };
}

public sealed class ProfileException(string code) : Exception(ProfileErrors.Message(code) ?? code)
{
    public string Code { get; } = code;
}
