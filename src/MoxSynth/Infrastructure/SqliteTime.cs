using System.Globalization;

namespace MoxSynth.Infrastructure;

/// <summary>
/// All timestamps in the collection db are stored as UTC TEXT in a fixed-width format,
/// so they sort and compare correctly as strings inside SQLite.
/// </summary>
public static class SqliteTime
{
    public const string Format = "yyyy-MM-dd HH:mm:ss.fffffff";

    public static string ToDb(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    public static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? AsUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

    /// <summary>Formats a UTC timestamp in the server's local time zone for display.</summary>
    public static string Display(DateTime? utc) =>
        utc is null ? "" : AsUtc(utc.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
