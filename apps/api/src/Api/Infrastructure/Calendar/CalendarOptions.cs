using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// Everything the calendar connection needs from configuration, bound from the
/// <c>Calendar</c> section — so <c>Calendar__RedirectUri</c> and friends as environment
/// variables (see <c>.env.example</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately separate from <see cref="Auth.GoogleOptions"/>, sharing its client.</b> The
/// OAuth client id and secret are the same ones the sign-in path uses — it is one application
/// asking for a second permission (design K1), and registering a second client would ask the
/// professional to trust two things where there is one. What is separate is everything that
/// makes this flow a different flow: its own redirect URI, its own scope, its own state cookie
/// lifetime, and the key that protects what it brings back.
/// </para>
/// <para>
/// The separation is load-bearing rather than tidy. <c>GoogleOptions.IsConfigured</c> is what
/// makes an absent Google client a <em>supported</em> configuration — the app starts, internal
/// accounts sign in, CI needs no secrets. Folding the encryption key into that predicate would
/// make the entire federated login path depend on a key that only the calendar needs.
/// </para>
/// <para>
/// <see cref="IsPresent"/> is the switch <see cref="CalendarOptionsValidator"/> reads: a
/// deployment that has not configured the calendar at all starts exactly as before, while one
/// that has, and has no usable key, refuses to start (design K4).
/// </para>
/// </remarks>
internal sealed class CalendarOptions
{
    internal const string SectionName = "Calendar";

    /// <summary>
    /// The exact redirect URI registered in the Google Console for the calendar flow.
    /// </summary>
    /// <remarks>
    /// A <b>second</b> registered URI, not the sign-in one. Sharing it would mean one callback
    /// path serving two flows with opposite obligations — the thing design K2 exists to prevent —
    /// and Google would happily deliver a calendar code to the endpoint that mints sessions.
    /// Locally <c>http://localhost:8080/api/calendar/connect/callback</c>.
    /// </remarks>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Base64 of the 32 random bytes that protect the stored refresh token (design K3).
    /// </summary>
    /// <remarks>
    /// A credential, not a setting: it lives in the environment or in Docker secrets and never
    /// in the repository. <b>It must survive redeploys.</b> Losing it does not corrupt anything,
    /// but every stored token becomes unreadable and every professional has to reconnect —
    /// which is why <c>.env.example</c> says so in as many words.
    /// </remarks>
    public string? TokenEncryptionKey { get; set; }

    /// <summary>
    /// The scope requested when a professional connects.
    /// </summary>
    /// <remarks>
    /// <c>calendar.events</c> rather than the full <c>calendar</c> scope: least privilege, and
    /// it is genuinely sufficient for both directions of this integration — 6b writes events and
    /// change 7 reads them. The wider scope would additionally permit creating and deleting whole
    /// calendars, which nothing in this product will ever do.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string Scope { get; set; } = "https://www.googleapis.com/auth/calendar.events";

    /// <summary>Where a grant is handed back when a professional disconnects (design K9).</summary>
    [Required(AllowEmptyStrings = false)]
    public string RevocationEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";

    /// <summary>
    /// The calendar an event would be written to, recorded rather than assumed (design K13).
    /// </summary>
    /// <remarks>
    /// <c>primary</c>, and no screen chooses otherwise. It is stored on the connection so 6b
    /// addresses a column instead of hard-coding a string in a dispatcher, which is the version
    /// of this that cannot be changed later without a migration.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string TargetCalendarId { get; set; } = "primary";

    /// <summary>How long a connect attempt may sit half-finished before its state expires.</summary>
    /// <remarks>
    /// The same ten minutes the sign-in flow allows, for the same reason and by the same
    /// mechanism: the cookie expires in the browser, so an abandoned flow needs no sweep to
    /// clean up after it (design K15). This is why the absence of a scheduler costs nothing here.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan ConnectStateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Google's authorization endpoint. Same as the sign-in flow's; different request.</summary>
    [Required(AllowEmptyStrings = false)]
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>Google's token endpoint, where the authorization code becomes a refresh token.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Whether this deployment has configured the calendar feature at all.
    /// </summary>
    /// <remarks>
    /// Keyed on the redirect URI because that is the value an operator cannot get from a default:
    /// it has to match a registration in the Google Console exactly, so its presence is a
    /// reliable statement of intent. The encryption key is deliberately NOT part of this
    /// predicate — a present feature with no key is the misconfiguration the validator refuses,
    /// and including it here would turn that error into a silent disabling.
    /// </remarks>
    public bool IsPresent => !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>
/// Refuses to start when the calendar is configured without a usable encryption key (design K4).
/// </summary>
/// <remarks>
/// The rule is conditional, and the two halves are different kinds of fact:
/// <list type="bullet">
/// <item>No calendar configuration → the feature is off, S2 reports it, everything else runs.
/// That is a supported deployment.</item>
/// <item>Calendar configured, key absent or the wrong size → startup fails. That is a
/// misconfiguration, and the failure mode it prevents is a refresh token sitting in the database
/// in clear while every screen reports success.</item>
/// </list>
/// The same shape as <c>ClinicTimeOptionsValidator</c>: a value whose absence would make the
/// system quietly wrong is a startup failure, never a default.
/// </remarks>
internal sealed class CalendarOptionsValidator : IValidateOptions<CalendarOptions>
{
    /// <summary>AES-256. Not negotiable, and not silently padded to fit.</summary>
    internal const int KeyByteLength = 32;

    public ValidateOptionsResult Validate(string? name, CalendarOptions options)
    {
        if (!options.IsPresent)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.TokenEncryptionKey))
        {
            return ValidateOptionsResult.Fail(
                "Calendar__TokenEncryptionKey is not configured, but the calendar feature is " +
                "(Calendar__RedirectUri is set). The refresh token is a long-lived credential and " +
                "is never stored unencrypted — see .env.example for how to generate a key.");
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(options.TokenEncryptionKey);
        }
        catch (FormatException)
        {
            return ValidateOptionsResult.Fail(
                "Calendar__TokenEncryptionKey is not valid base64. Generate one with the command " +
                "in .env.example rather than typing a passphrase.");
        }

        if (key.Length != KeyByteLength)
        {
            return ValidateOptionsResult.Fail(
                $"Calendar__TokenEncryptionKey decodes to {key.Length} bytes; AES-256 needs " +
                $"exactly {KeyByteLength}. Generate one with the command in .env.example.");
        }

        return ValidateOptionsResult.Success;
    }
}
