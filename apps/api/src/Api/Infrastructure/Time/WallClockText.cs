using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Infrastructure.Time;

/// <summary>
/// Parses and formats the wall-clock strings S7 exchanges.
/// </summary>
/// <remarks>
/// Moved here from the <c>AdminConfig</c> slice by <c>availability-read</c>, which needs the same
/// parsing for S3's block times. A shared text primitive sitting inside one feature folder is how
/// a vertical slice starts reaching into its neighbour, and the honest fix is to move it rather
/// than reference it across the seam.
///
/// Explicit patterns rather than <c>DateTime.Parse</c>, for one reason: the framework parsers
/// happily accept an offset or a trailing <c>Z</c> and then apply it. Accepting <c>"09:00-03:00"</c>
/// as a working hour would silently reintroduce the timezone this data must not carry (design E3).
/// These patterns accept exactly one shape and refuse everything else.
/// </remarks>
internal static class WallClockText
{
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;

    /// <summary>
    /// The shape a browser's <c>datetime-local</c> input produces, with and without seconds.
    /// </summary>
    /// <remarks>
    /// Two patterns because the control is not consistent about seconds across browsers, and one
    /// that also refuses an offset for the same reason the time pattern does: accepting
    /// <c>"2026-08-25T14:00-03:00"</c> would let a caller decide which zone a clinic time meant.
    /// The clinic's configured zone is the only interpretation, applied server-side.
    /// </remarks>
    private static readonly LocalDateTimePattern[] DateTimePatterns =
    [
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm"),
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss"),
    ];

    internal static string Format(LocalTime time) => TimePattern.Format(time);

    internal static string Format(LocalDateTime dateTime) => DateTimePatterns[0].Format(dateTime);

    /// <summary>Parses <c>"yyyy-MM-ddTHH:mm"</c> (seconds optional), returning null for anything else.</summary>
    internal static LocalDateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var pattern in DateTimePatterns)
        {
            var result = pattern.Parse(value);

            if (result.Success)
            {
                return result.Value;
            }
        }

        return null;
    }

    internal static string Format(LocalDate date) => DatePattern.Format(date);

    /// <summary>Parses <c>"HH:mm"</c>, returning null for anything else.</summary>
    internal static LocalTime? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = TimePattern.Parse(value);

        return result.Success ? result.Value : null;
    }

    /// <summary>Parses <c>"yyyy-MM-dd"</c>, returning null for anything else.</summary>
    internal static LocalDate? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = DatePattern.Parse(value);

        return result.Success ? result.Value : null;
    }

    /// <summary>Parses an English weekday name, returning null for anything else.</summary>
    internal static IsoDayOfWeek? ParseDayOfWeek(string? value) =>
        Enum.TryParse<IsoDayOfWeek>(value, ignoreCase: true, out var day) && day != IsoDayOfWeek.None
            ? day
            : null;
}
