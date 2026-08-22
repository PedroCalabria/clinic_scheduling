using NodaTime;
using NodaTime.Text;

namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// Parses and formats the wall-clock strings S7 exchanges.
/// </summary>
/// <remarks>
/// Explicit patterns rather than <c>DateTime.Parse</c>, for one reason: the framework parsers
/// happily accept an offset or a trailing <c>Z</c> and then apply it. Accepting <c>"09:00-03:00"</c>
/// as a working hour would silently reintroduce the timezone this data must not carry (design E3).
/// These patterns accept exactly one shape and refuse everything else.
/// </remarks>
internal static class WallClockText
{
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;

    internal static string Format(LocalTime time) => TimePattern.Format(time);

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
