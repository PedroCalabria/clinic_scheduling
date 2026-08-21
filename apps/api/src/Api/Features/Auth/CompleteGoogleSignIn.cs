using Microsoft.AspNetCore.WebUtilities;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Auth.Google;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>GET /api/auth/google/callback</c> — where a Google sign-in becomes a session, and where
/// the provisioning rule lives (design A5).
/// </summary>
/// <remarks>
/// <para>
/// The order is: prove the callback belongs to a sign-in this browser started (<c>state</c>),
/// exchange the code, prove the token belongs to that same request (<c>nonce</c>), insist the
/// email is verified, and only then decide which user this is.
/// </para>
/// <para>
/// <c>email_verified</c> is load-bearing, not a formality. The invite-claim rule matches a
/// prepared professional BY EMAIL, so an unverified address would let anyone able to set an
/// arbitrary email claim take a prepared account. This one check is what makes email-based
/// claiming safe (design A4).
/// </para>
/// <para>
/// Failures return the browser to the sign-in surface with the code in the query, rather than
/// writing a JSON body into a top-level navigation. The reason is a user-facing one: at least
/// one refusal here — a staff member with an internal account clicking "Sign in with Google" —
/// is an ordinary mistake, not an attack, and it deserves a translated sentence rather than a
/// raw response. The destination is always a path derived from the state cookie, never taken
/// from the request, so the error path cannot be turned into an open redirect.
/// </para>
/// </remarks>
internal static class CompleteGoogleSignIn
{
    /// <summary>Query parameter the frontends read to show a translated sign-in failure.</summary>
    internal const string ErrorQueryParameter = "authError";

    internal static RouteHandlerBuilder MapCompleteGoogleSignIn(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/auth/google/callback", HandleAsync)
            .AllowAnonymous()
            .WithName("CompleteGoogleSignIn");

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ClinicDbContext database,
        SessionStore sessions,
        GoogleTokenExchange tokenExchange,
        IGoogleIdTokenValidator tokenValidator,
        IOptions<AuthOptions> options,
        TimeProvider clock,
        ILogger<GoogleCallbackMarker> logger,
        string? code = null,
        string? state = null)
    {
        var pending = GoogleOAuthState.FromCookieValue(context.Request.Cookies[AuthCookies.OAuthState]);

        // Consumed the moment it is read: a replay of this callback finds no cookie to match,
        // which is what makes the single-use property real (design A3).
        AuthCookies.DeleteOAuthState(context.Response);

        var returnPath = pending?.ReturnPath ?? GoogleOAuthState.DefaultReturnPath;

        if (!options.Value.Google.IsConfigured)
        {
            logger.LogWarning("Google callback reached but no Google client is configured.");

            return Failure(returnPath, ErrorCodes.GoogleUnavailable);
        }

        if (pending is null || !pending.MatchesState(state))
        {
            // Either the flow was never started in this browser, its state expired, or somebody
            // is injecting a callback. All three are the same answer.
            logger.LogWarning("Google callback rejected: state missing or mismatched.");

            return Failure(returnPath, ErrorCodes.GoogleFailed);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            logger.LogWarning("Google callback rejected: no authorization code.");

            return Failure(returnPath, ErrorCodes.GoogleFailed);
        }

        var idToken = await tokenExchange.ExchangeForIdTokenAsync(code, context.RequestAborted);

        if (idToken is null)
        {
            return Failure(returnPath, ErrorCodes.GoogleFailed);
        }

        var identity = await tokenValidator.ValidateAsync(idToken, pending.Nonce, context.RequestAborted);

        if (identity is null)
        {
            return Failure(returnPath, ErrorCodes.GoogleFailed);
        }

        if (!identity.EmailVerified)
        {
            logger.LogWarning("Google sign-in rejected: the provider reports the email as unverified.");

            return Failure(returnPath, ErrorCodes.GoogleFailed);
        }

        var resolution = await ResolveUserAsync(identity, database, options.Value, clock, logger, context.RequestAborted);

        if (resolution.User is null)
        {
            return Failure(returnPath, resolution.ErrorCode!);
        }

        var user = resolution.User;

        if (!user.CanAuthenticate)
        {
            logger.LogWarning("Google sign-in refused for {UserId}: account status is {Status}.", user.Id, user.Status);

            return Failure(returnPath, ErrorCodes.AccountDisabled);
        }

        user.RecordSuccessfulSignIn();
        await database.SaveChangesAsync(context.RequestAborted);

        var (token, expiresAtUtc) = await sessions.IssueAsync(user, context.RequestAborted);
        context.Response.Cookies.Append(AuthCookies.Session, token, AuthCookies.ForSession(expiresAtUtc));

        logger.LogInformation("Google sign-in succeeded for {UserId} with role {Role}.", user.Id, user.Role);

        return Results.Redirect(returnPath);
    }

    /// <summary>
    /// The provisioning decision (design A5): known subject, prepared invitation, or a new
    /// patient — and the refusal that protects internal accounts.
    /// </summary>
    private static async Task<(User? User, string? ErrorCode)> ResolveUserAsync(
        GoogleIdentity identity,
        ClinicDbContext database,
        AuthOptions options,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 1. Somebody who has signed in before. Nothing about them changes.
        var bySubject = await database.Users.SingleOrDefaultAsync(
            user => user.AuthProvider == AuthProvider.Google
                && user.ExternalSubjectId == identity.Subject
                && user.DeletedAtUtc == null,
            cancellationToken);

        if (bySubject is not null)
        {
            return (bySubject, null);
        }

        var email = EmailAddress.Normalize(identity.Email);

        var byEmail = await database.Users.SingleOrDefaultAsync(
            user => user.Email == email && user.DeletedAtUtc == null,
            cancellationToken);

        if (byEmail is not null)
        {
            // 2. An account already holds this address. Only a federated one awaiting a claim
            // may be taken over by it — the domain enforces the rest, and this is the refusal
            // that stops a staff mailbox from being a staff login.
            if (byEmail.AuthProvider != AuthProvider.Google || !byEmail.AwaitsClaim)
            {
                logger.LogWarning(
                    "Google sign-in refused for {UserId}: the address belongs to a {Provider} account that cannot be claimed.",
                    byEmail.Id, byEmail.AuthProvider);

                return (null, ErrorCodes.GoogleFailed);
            }

            byEmail.ClaimWithGoogleIdentity(identity.Subject);
            await database.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Professional invitation {UserId} claimed by its first Google sign-in.", byEmail.Id);

            return (byEmail, null);
        }

        // 3. Nobody has this address: a patient, provisioned just in time, with the consent that
        // makes processing their data lawful recorded in the same transaction.
        var now = clock.GetUtcNow();
        var newUser = User.RegisterGooglePatient(email, identity.Subject, now);
        var patient = Patient.Register(newUser.Id, identity.FullName, email, now);
        var consent = Consent.Grant(newUser.Id, ConsentType.DataProcessing, options.ConsentVersion, now);

        database.Users.Add(newUser);
        database.Patients.Add(patient);
        database.Consents.Add(consent);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Provisioned patient {UserId} from a first Google sign-in.", newUser.Id);

        return (newUser, null);
    }

    private static IResult Failure(string returnPath, string errorCode) =>
        Results.Redirect(QueryHelpers.AddQueryString(returnPath, ErrorQueryParameter, errorCode));

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class GoogleCallbackMarker;
}
