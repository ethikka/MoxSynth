using MoxSynth.Infrastructure;

namespace MoxSynth.Profiles;

/// <summary>A person using MoxSynth. Every profile has its own collection database.</summary>
public sealed class Profile
{
    private DateTime _createdAt;

    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Avatar colour (CSS colour).</summary>
    public string Color { get; set; } = ProfileColors.All[0];
    /// <summary>Admins can create, rename and delete profiles.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>File name of the collection database, relative to the collections folder (or absolute for the legacy db).</summary>
    public string DbFile { get; set; } = "";
    public DateTime CreatedAt { get => _createdAt; set => _createdAt = SqliteTime.AsUtc(value); }

    /// <summary>PBKDF2 hash (see <see cref="PasswordHashing"/>); null for a profile adopted from before passwords existed.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Changes whenever the password changes; stored in the sign-in cookie so old sessions stop working.</summary>
    public string? PasswordStamp { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
}

public static class ProfileColors
{
    public static readonly IReadOnlyList<string> All =
    [
        "#e03131", "#f08c00", "#2f9e44", "#1971c2", "#7048e8", "#c2255c", "#0c8599", "#5c940d", "#495057",
    ];
}

/// <summary>
/// The profile of the current browser (circuit-scoped). Set once by <c>Routes</c> from the profile cookie;
/// <see cref="Collection.CollectionRepository"/> uses it to pick the right database.
/// </summary>
public sealed class CurrentProfile
{
    public Profile? Value { get; private set; }

    public bool IsSelected => Value is not null;
    public bool IsAdmin => Value?.IsAdmin == true;

    /// <summary>Raised when the profile changes (e.g. renamed by an admin) so the navbar can refresh.</summary>
    public event Action? Changed;

    public void Set(Profile? profile)
    {
        Value = profile;
        Changed?.Invoke();
    }
}
