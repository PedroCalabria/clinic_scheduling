using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Clinic.Api.Infrastructure.Time;

/// <summary>
/// The clinic's own timezone (Decision H), bound from the <c>Clinic</c> section — so
/// <c>Clinic__Timezone</c> as an environment variable (see <c>.env.example</c>).
/// </summary>
/// <remarks>
/// Required, with no default. A default here would be wrong for every clinic but one, and
/// wrong silently: working hours would be recorded correctly and interpreted against the
/// wrong zone, which surfaces as appointments an hour out rather than as an error.
/// </remarks>
internal sealed class ClinicTimeOptions
{
    internal const string SectionName = "Clinic";

    /// <summary>An IANA zone id, e.g. <c>America/Sao_Paulo</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Timezone { get; set; } = string.Empty;
}

/// <summary>
/// Refuses a zone id the zone database does not recognize.
/// </summary>
/// <remarks>
/// Data annotations can say "present"; only the tzdb can say "real". A typo like
/// <c>America/SaoPaulo</c> is exactly the kind of value that passes a presence check and then
/// fails at the first conversion, which is change 4 rather than here — so it is caught at
/// startup, and the message names the setting so the operator knows which line to edit.
/// </remarks>
internal sealed class ClinicTimeOptionsValidator : IValidateOptions<ClinicTimeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClinicTimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Timezone))
        {
            return ValidateOptionsResult.Fail(
                "Clinic__Timezone is not configured. Set it to an IANA zone id such as " +
                "'America/Sao_Paulo' — see .env.example.");
        }

        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(options.Timezone) is null)
        {
            return ValidateOptionsResult.Fail(
                $"Clinic__Timezone '{options.Timezone}' is not a recognized IANA zone id. " +
                "Use a value such as 'America/Sao_Paulo' — see .env.example.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// The clinic's timezone, resolved once.
/// </summary>
/// <remarks>
/// A resolved <see cref="DateTimeZone"/> rather than the raw string, so nothing downstream
/// re-parses configuration and no two callers can disagree about what the string meant. This
/// is the seam change 4's solver reads when it converts a wall-clock working hour against a
/// concrete date; nothing in <c>professional-configuration</c> converts anything (design E3).
/// </remarks>
internal sealed class ClinicTimezone
{
    public ClinicTimezone(IOptions<ClinicTimeOptions> options)
    {
        // Validated at startup, so a null here would be a programming error rather than a
        // configuration one.
        Zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(options.Value.Timezone)
            ?? throw new InvalidOperationException(
                $"Clinic timezone '{options.Value.Timezone}' resolved to nothing after validation.");
    }

    /// <summary>The zone every wall-clock time in this system is expressed in.</summary>
    public DateTimeZone Zone { get; }

    /// <summary>The configured id, for logging and for reporting what the clinic runs on.</summary>
    public string Id => Zone.Id;
}
