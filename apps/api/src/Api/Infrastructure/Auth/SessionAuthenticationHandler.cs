using System.Security.Claims;
using System.Text.Encodings.Web;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Claim types this API puts on the principal. Nothing authorization-bearing beyond the
/// role, because the row — not the cookie — is the authority (design A1).
/// </summary>
internal static class ClinicClaims
{
    /// <summary>The session's own id, so a handler can revoke the current session.</summary>
    internal const string SessionId = "clinic:sid";

    /// <summary>Present only while the bootstrap credential has not been replaced (design A6).</summary>
    internal const string MustChangePassword = "clinic:must_change_password";
}

/// <summary>
/// Turns the session cookie into a <see cref="ClaimsPrincipal"/> — and does nothing else
/// (design A1).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the custom authentication surface. Authorization is not reimplemented
/// here: <c>[Authorize]</c>, policies, and role checks come from the framework, which is the
/// point of writing a handler instead of hand-rolling the pipeline. Full ASP.NET Core
/// Identity was rejected because it brings its own user schema that collides with the one
/// 02-domain-model.md specifies; framework cookie authentication was rejected because it
/// keeps a signed copy of the principal in the cookie, so revocation becomes a validation
/// hook that hits the database anyway — the same cost with a staleness bug attached.
/// </para>
/// <para>
/// The two overridden failure paths matter as much as the success path: without them the
/// framework answers an empty 401 or 403, and the frontend has nothing to translate
/// (Decision I).
/// </para>
/// </remarks>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ClinicSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[AuthCookies.Session];

        if (string.IsNullOrEmpty(token))
        {
            // No credential presented at all. NoResult rather than Fail: an anonymous request
            // to an anonymous endpoint is not an error.
            return AuthenticateResult.NoResult();
        }

        var principal = await sessions.ResolveAsync(token, Context.RequestAborted);

        if (principal is null)
        {
            // Unknown, expired, revoked, or the account can no longer authenticate. The four
            // are one outcome on purpose: telling them apart would tell a caller which
            // sessions exist.
            return AuthenticateResult.Fail("The session is not usable.");
        }

        var identity = new ClaimsIdentity(BuildClaims(principal), SchemeName, ClaimTypes.NameIdentifier, ClaimTypes.Role);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Answers an unauthenticated request with the catalogue code, not an empty body.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiError.WriteAsync(
            Response,
            ErrorCodes.SessionExpired,
            StatusCodes.Status401Unauthorized,
            cancellationToken: Context.RequestAborted);

    /// <summary>
    /// Answers a role refusal. Distinct from the challenge above, which is what makes
    /// "authenticated but forbidden" distinguishable from "not authenticated" — and distinct
    /// again from ownership refusals, which the slices answer with
    /// <see cref="ErrorCodes.OwnershipDenied"/>.
    /// </summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiError.WriteAsync(
            Response,
            ErrorCodes.Forbidden,
            StatusCodes.Status403Forbidden,
            cancellationToken: Context.RequestAborted);

    private static IEnumerable<Claim> BuildClaims(SessionPrincipal principal)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, principal.UserId.ToString());
        yield return new Claim(ClaimTypes.Role, principal.Role.ToString());
        yield return new Claim(ClinicClaims.SessionId, principal.SessionId.ToString());

        if (principal.MustChangePassword)
        {
            yield return new Claim(ClinicClaims.MustChangePassword, "true");
        }
    }
}

/// <summary>
/// Reads this API's claims back off a principal, so no slice parses claim strings by hand.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The acting user's id, from the session. This is the only source of identity the
    /// authorization rules ever use — never a value from the request body or route.
    /// </summary>
    internal static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The principal carries no user id; the endpoint is not authenticated.");

    internal static Role Role(this ClaimsPrincipal principal) =>
        Enum.TryParse<Role>(principal.FindFirstValue(ClaimTypes.Role), out var role)
            ? role
            : throw new InvalidOperationException("The principal carries no role.");

    internal static Guid SessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClinicClaims.SessionId), out var id)
            ? id
            : throw new InvalidOperationException("The principal carries no session id.");

    internal static bool MustChangePassword(this ClaimsPrincipal principal) =>
        principal.HasClaim(ClinicClaims.MustChangePassword, "true");
}
