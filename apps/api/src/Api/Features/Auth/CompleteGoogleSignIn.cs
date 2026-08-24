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
/// One callback, two surfaces. Which rule applies is decided by the surface the flow was
/// STARTED from, carried in the state cookie (design D1). Each surface admits only the role it
/// serves — the portal a patient, the console a professional — and they differ in one further
/// respect: an address with no account at all becomes a patient on the portal and is refused on
/// the console. So the same Google identity can be a legitimate patient on P1 and a refused
/// stranger on S0.
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

        var resolution = await ResolveUserAsync(
            identity, pending.Surface, database, options.Value, clock, logger, context.RequestAborted);

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
    /// patient — the refusal that protects internal accounts, and the surface that decides
    /// whether provisioning may happen at all (design D2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <paramref name="surface"/> is where <c>staff-google-guard</c> lands. Change 2 wrote
    /// one rule for one flow, before either frontend existed as something a person could click,
    /// and the create-a-patient step at the end was unconditional. It therefore ran for S0 too:
    /// a professional who signed in before being invited became a patient, and was then
    /// un-invitable because their address was taken.
    /// </para>
    /// <para>
    /// The order below is the fix as much as the branch is. Every refusal is decided BEFORE
    /// anything is written, because the damage was never the refusal — it was the row.
    /// </para>
    /// </remarks>
    private static async Task<(User? User, string? ErrorCode)> ResolveUserAsync(
        GoogleIdentity identity,
        SignInSurface surface,
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
            return AdmitOnSurface(bySubject, surface, logger);
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

            // Checked before the claim is written, not after — and this is where that ordering
            // stops being theoretical. An invitation is a professional, so reaching it from the
            // PORTAL is a wrong-door sign-in: without this check the invitation would be claimed
            // and the subject id bound before anyone noticed the surface was wrong.
            var admission = AdmitOnSurface(byEmail, surface, logger);

            if (admission.User is null)
            {
                return admission;
            }

            byEmail.ClaimWithGoogleIdentity(identity.Subject);
            await database.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Professional invitation {UserId} claimed by its first Google sign-in.", byEmail.Id);

            return (byEmail, null);
        }

        // 3. Nobody has this address. On the staff console that is the end of it: S0 offers "the
        // account the clinic registered for you", so an address nobody registered is an
        // un-invited professional, not a new patient. Refused here, before the create below,
        // which is the whole of this change.
        if (surface == SignInSurface.Staff)
        {
            logger.LogWarning(
                "Google sign-in on the staff surface refused: no account is registered for that address. "
                + "Nothing was created.");

            return (null, ErrorCodes.NotProvisioned);
        }

        // 4. On the patient portal, nobody having this address means a patient, provisioned just
        // in time, with the consent that makes processing their data lawful recorded in the same
        // transaction. Unchanged from change 2 — this is the surface that is supposed to do it.
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

    /// <summary>
    /// Each surface admits the one role it serves, and sends everyone else to their own door
    /// (design D2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whitelist per surface rather than a list of exclusions, so a role added later is refused
    /// by default instead of admitted by omission.
    /// </para>
    /// <para>
    /// Both directions matter, and the second was found by running this change's own validation
    /// guide. A professional signing in on the PORTAL used to get a session — and then P7 told
    /// them "no such patient record", because a professional has no patient row. Worse, an
    /// unclaimed invitation was claimed through the wrong door on the way. The staff console had
    /// the mirror of it: a patient could hold a session in which every screen is forbidden.
    /// </para>
    /// <para>
    /// Both are the same defect: a surface establishing a session for someone it cannot serve.
    /// So the rule is symmetric, and the refusal names the door that IS theirs rather than
    /// reporting a generic failure — a person who clicked the wrong entrance needs a direction,
    /// not a diagnosis.
    /// </para>
    /// </remarks>
    private static (User? User, string? ErrorCode) AdmitOnSurface(
        User user,
        SignInSurface surface,
        ILogger logger)
    {
        var (admits, wrongDoor) = surface switch
        {
            SignInSurface.Staff => (Role.Professional, ErrorCodes.UsePatientSignIn),

            // The portal's refusal names the professional role because that is the only one that
            // can reach it: an internal account is stopped earlier by the takeover defence
            // (`auth.google_failed`), and a patient is admitted. Revisit the message if a third
            // Google-provider role is ever introduced.
            _ => (Role.Patient, ErrorCodes.UseStaffSignIn),
        };

        if (user.Role == admits)
        {
            return (user, null);
        }

        // The id and the role, never the address: an operator needs to see that a wrong-door
        // sign-in was attempted, not to have a mailbox written into the logs.
        logger.LogWarning(
            "Google sign-in on the {Surface} surface refused for {UserId}: role {Role} is not served there.",
            surface, user.Id, user.Role);

        return (null, wrongDoor);
    }

    private static IResult Failure(string returnPath, string errorCode) =>
        Results.Redirect(QueryHelpers.AddQueryString(returnPath, ErrorQueryParameter, errorCode));

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class GoogleCallbackMarker;
}
