using System.ComponentModel.DataAnnotations;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Everything about authentication that belongs in configuration rather than in code,
/// bound from the <c>Auth</c> section (so <c>Auth__SessionLifetime</c> and friends as
/// environment variables — see <c>.env.example</c>).
/// </summary>
/// <remarks>
/// The split is deliberate: the rules live in the domain and the slices, the numbers live
/// here. <see cref="LockoutThreshold"/> is the clearest example — "too many failures locks
/// the account" is a domain rule, while "five" is an operational choice.
/// </remarks>
internal sealed class AuthOptions
{
    internal const string SectionName = "Auth";

    /// <summary>
    /// How long a session stays valid from the moment it is issued.
    /// </summary>
    /// <remarks>
    /// A fixed absolute lifetime, with no sliding renewal (design Open Questions). Sliding
    /// expiry is the cheaper thing to add later than to take away: it would mean writing to
    /// the session row on nearly every request, which is a real cost to accept only once
    /// somebody is actually annoyed by being signed out.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:05:00", "30.00:00:00")]
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Consecutive failed sign-ins that lock an account (design A10).</summary>
    [Range(1, 100)]
    public int LockoutThreshold { get; set; } = 5;

    /// <summary>
    /// Login attempts allowed per client address per minute (design A10).
    /// </summary>
    /// <remarks>
    /// Configuration for the same reason as <see cref="LockoutThreshold"/>: "the login path is
    /// rate-limited" is the design, "ten a minute" is an operational choice. It also has to be
    /// settable for the tests that prove the limiter refuses — and for the far larger number of
    /// tests that must not trip over it.
    /// </remarks>
    [Range(1, 10_000)]
    public int LoginAttemptsPerMinute { get; set; } = 10;

    /// <summary>
    /// Shortest password an internal account may set.
    /// </summary>
    /// <remarks>
    /// Length only. Composition rules (a digit, a symbol, a capital) push people towards
    /// predictable substitutions and are no longer recommended practice; length is the
    /// property that actually costs an attacker something.
    /// </remarks>
    [Range(8, 200)]
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>
    /// Version recorded on a consent granted from now on.
    /// </summary>
    /// <remarks>
    /// Configuration rather than a database row, because no screen edits consent text at
    /// runtime; a table would be storage for a value that only ever changes with a deploy.
    /// </remarks>
    [Required]
    public string ConsentVersion { get; set; } = "2026-08";

    /// <summary>The administrator created when none exists (design A6).</summary>
    public BootstrapAdministratorOptions BootstrapAdministrator { get; set; } = new();

    /// <summary>Google OIDC client configuration. Absent means the federated path is off.</summary>
    public GoogleOptions Google { get; set; } = new();
}

/// <summary>
/// The first-administrator bootstrap (design A6) — the escape from "S11 creates
/// administrators, but only an administrator can open S11".
/// </summary>
internal sealed class BootstrapAdministratorOptions
{
    public string? Email { get; set; }

    /// <summary>
    /// A real credential, not a setting. It lives in the environment, never in the repo,
    /// and the account it creates must change it on first sign-in.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>True when both values are present, so bootstrap has something to do.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// Google OIDC client configuration (design A6, A14).
/// </summary>
/// <remarks>
/// Optional on purpose. If it is missing, the app starts, the internal login path works,
/// and the Google endpoints answer a configuration error — a contributor can run and test
/// everything except the live federated path with no Google project, and CI needs no
/// secrets because the token seams cover that path.
///
/// Scopes are identity only. The calendar scope is requested in change 6 through
/// incremental authorization (design A6), which is why there is no scope setting here to
/// widen by accident.
/// </remarks>
internal sealed class GoogleOptions
{
    /// <summary>Google's issuer, and the expected <c>iss</c> of every ID token.</summary>
    public string Issuer { get; set; } = "https://accounts.google.com";

    /// <summary>Where the OpenID configuration (and through it, the JWKS) is discovered.</summary>
    public string MetadataAddress { get; set; } = "https://accounts.google.com/.well-known/openid-configuration";

    /// <summary>Google's token endpoint, where the authorization code is exchanged.</summary>
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>Google's authorization endpoint, where the browser is sent.</summary>
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// The exact redirect URI registered in the Google Console. Locally
    /// <c>http://localhost:8080/api/auth/google/callback</c> — Google permits plain HTTP for
    /// <c>localhost</c>, so no tunnel is needed here (that is change 7's problem).
    /// </summary>
    public string? RedirectUri { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
