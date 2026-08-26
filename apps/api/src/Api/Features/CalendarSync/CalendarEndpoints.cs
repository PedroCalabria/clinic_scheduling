using System.Security.Claims;
using Clinic.Api.Features.AdminConfig;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Calendar;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Clinic.Domain;
using Clinic.Domain.Calendar;
using Clinic.Domain.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Features.CalendarSync;

/// <summary>
/// S2 — a professional connects, checks, and withdraws access to their own calendar
/// (spec: calendar-integration; change 6a).
/// </summary>
/// <remarks>
/// <para>
/// <b>This slice writes nothing to any calendar.</b> It establishes the authorization that 6b's
/// outbox will ride on, and stops there. No event is created, updated or deleted; no appointment
/// gains an external reference; availability is untouched. If a future reader finds calendar
/// content being read or written here, it arrived in the wrong change.
/// </para>
/// <para>
/// <b>Ownership by unreachability</b> (design K11). No route carries an identifier: the
/// professional is resolved from the principal, so there is no request shape by which one
/// professional could name another's connection. That is stronger than a check, and it is why
/// <see cref="PatientDataGuard"/> is not involved — that guard exists for resources that
/// <em>must</em> be addressable by id. Nothing here writes an <c>AccessLog</c> row either: a
/// calendar connection is the professional's own record and holds no patient data, and widening
/// that trail would dilute an audit whose value is its narrowness. The same argument S3 made.
/// </para>
/// <para>
/// <b>Two flows, kept apart on purpose</b> (design K2). The endpoints below live under
/// <c>/api/calendar</c>, use their own state cookie, and require an authenticated professional.
/// The sign-in callback one folder away does the opposite of all three — it is anonymous, it
/// establishes a session, and it may create a user. Sharing a route or a cookie between them
/// would put a session-minting path one mistaken branch away from a code that arrived here.
/// </para>
/// <para>
/// A professional whose clinical configuration does not exist yet is refused the same way S3
/// refuses them: change 2 invites a professional and 3b's S7 creates their record on first save,
/// so a claimed invitation can sit in between (design E1). The connection is keyed on the
/// <c>Professional</c> row per <c>02-domain-model.md</c> §4, which answers this change's design
/// Open Question 3 by following the precedent rather than inventing a second answer.
/// </para>
/// </remarks>
internal static class CalendarEndpoints
{
    /// <summary>How the browser is told the connect flow failed, mirroring the sign-in flow.</summary>
    internal const string ErrorQueryParameter = "calendarError";

    /// <summary>How the browser is told it worked, so S2 can confirm rather than infer.</summary>
    internal const string ConnectedQueryParameter = "calendarConnected";

    internal static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/calendar")
            // The grant is the professional's to give. Reception and administration are refused
            // here, not hidden in the UI: an administrator connecting a calendar on somebody's
            // behalf is the one thing a consent cannot be.
            .RequireAuthorization(AuthorizationPolicies.Professional);

        group.MapGet("/connect", StartAsync).WithName("StartCalendarConnect");
        group.MapGet("/connect/callback", CallbackAsync).WithName("CompleteCalendarConnect");
        group.MapGet("/connection", ReadAsync).WithName("ReadCalendarConnection");
        group.MapPost("/connection/check", CheckAsync).WithName("CheckCalendarConnection");
        group.MapPost("/connection/disconnect", DisconnectAsync).WithName("DisconnectCalendar");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /api/calendar/connect</c> — sends the professional to Google (S2).
    /// </summary>
    /// <remarks>
    /// A server-side redirect for the same reason the sign-in flow uses one: the <c>state</c> has
    /// to be generated somewhere the browser cannot influence, and the cookie remembering it has
    /// to be <c>HttpOnly</c>.
    /// </remarks>
    private static async Task<IResult> StartAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        IOptions<AuthOptions> authOptions,
        IOptions<CalendarOptions> calendarOptions,
        TimeProvider clock,
        ILogger<CalendarMarker> logger,
        CancellationToken cancellationToken,
        string? returnTo = null)
    {
        var calendar = calendarOptions.Value;
        var returnPath = CalendarOAuthState.SafeReturnPath(returnTo);

        if (!calendar.IsPresent || !authOptions.Value.Google.IsConfigured)
        {
            // An operator fact, not a caller mistake — and the same fact the sign-in path
            // reports, so it reuses that code rather than minting a second name for "this
            // deployment has no Google client".
            logger.LogWarning("Calendar connect requested but the calendar feature is not configured.");

            return RedirectWithError(returnPath, ErrorCodes.GoogleUnavailable);
        }

        if (await ProfessionalIdAsync(database, actor, cancellationToken) is null)
        {
            return RedirectWithError(returnPath, ErrorCodes.ConfigNotFound);
        }

        var pending = CalendarOAuthState.Start(returnTo);

        context.Response.Cookies.Append(
            AuthCookies.CalendarState,
            pending.ToCookieValue(),
            AuthCookies.ForCalendarState(clock.GetUtcNow().Add(calendar.ConnectStateLifetime)));

        var authorizationUrl = QueryHelpers.AddQueryString(
            calendar.AuthorizationEndpoint,
            new Dictionary<string, string?>
            {
                ["client_id"] = authOptions.Value.Google.ClientId,
                ["redirect_uri"] = calendar.RedirectUri,
                ["response_type"] = "code",
                ["scope"] = calendar.Scope,
                ["state"] = pending.State,

                // Without this, Google issues a grant covering the calendar scope ALONE and
                // silently drops the identity grant obtained at sign-in. Nothing breaks visibly
                // at first — the session is ours, not Google's — so the damage surfaces later,
                // somewhere unrelated. StartGoogleSignIn has carried a comment warning about
                // this since change 2; a test now asserts the parameter, because a comment is
                // not a test (design K1).
                ["include_granted_scopes"] = "true",

                // A refresh token is the entire point of this flow.
                ["access_type"] = "offline",

                // Google returns a refresh token only on the FIRST grant for a client/user pair,
                // so a professional who disconnects and reconnects would otherwise complete a
                // successful authorization carrying no credential at all. This is the first of
                // the two guards against that; the second is in the aggregate, and it is the one
                // that survives a change in Google's behaviour (design K6).
                ["prompt"] = "consent",
            });

        return Results.Redirect(authorizationUrl);
    }

    /// <summary>
    /// <c>GET /api/calendar/connect/callback</c> — Google returns the professional here.
    /// </summary>
    /// <remarks>
    /// Establishes no session and creates no user. It cannot: the route requires an
    /// authenticated professional, so there is nobody to provision and nothing to sign in.
    /// </remarks>
    private static async Task<IResult> CallbackAsync(
        HttpContext context,
        ClaimsPrincipal actor,
        ClinicDbContext database,
        GoogleCalendarTokens tokens,
        CalendarTokenProtector protector,
        IOptions<AuthOptions> authOptions,
        IOptions<CalendarOptions> calendarOptions,
        TimeProvider clock,
        ILogger<CalendarMarker> logger,
        CancellationToken cancellationToken,
        string? code = null,
        string? state = null,
        string? error = null)
    {
        var calendar = calendarOptions.Value;
        var pending = CalendarOAuthState.FromCookieValue(context.Request.Cookies[AuthCookies.CalendarState]);
        var returnPath = pending?.ReturnPath ?? CalendarOAuthState.DefaultReturnPath;

        // Consumed whatever happens next, so a replay finds nothing to match against. Cleared
        // before any decision, because every path below is a path this flow is over.
        AuthCookies.DeleteCalendarState(context.Response);

        if (pending is null || !pending.MatchesState(state))
        {
            // Refused before any exchange is attempted: a code presented with state we did not
            // issue is not ours to spend.
            logger.LogWarning("Calendar callback presented state that did not match a pending authorization.");

            return RedirectWithError(returnPath, ErrorCodes.GoogleFailed);
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            // The professional pressed cancel on Google's screen, or Google refused. Reported as
            // a declined scope rather than a failure, because that is what it is from the
            // professional's side, and the action they need offered is "grant permission".
            logger.LogInformation("Calendar authorization was not granted: {Error}.", error ?? "no code returned");

            return RedirectWithError(returnPath, ErrorCodes.CalendarScopeDeclined);
        }

        if (!calendar.IsPresent || !authOptions.Value.Google.IsConfigured)
        {
            return RedirectWithError(returnPath, ErrorCodes.GoogleUnavailable);
        }

        if (await ProfessionalIdAsync(database, actor, cancellationToken) is not { } professionalId)
        {
            return RedirectWithError(returnPath, ErrorCodes.ConfigNotFound);
        }

        var grant = await tokens.ExchangeCodeAsync(code, cancellationToken);

        if (grant is null)
        {
            return RedirectWithError(returnPath, ErrorCodes.CalendarSyncFailed);
        }

        // The most likely real-world failure in this change, and invisible unless asked about:
        // Google's consent screen is granular, so a professional can approve the request and
        // untick calendar access while the token response stays perfectly valid (design K5).
        if (!grant.Includes(calendar.Scope))
        {
            logger.LogInformation("Calendar authorization completed without the calendar scope.");

            return RedirectWithError(returnPath, ErrorCodes.CalendarScopeDeclined);
        }

        var now = clock.GetUtcNow();
        var connection = await database.CalendarConnections
            .FirstOrDefaultAsync(candidate => candidate.ProfessionalId == professionalId, cancellationToken);

        var sealedCredential = grant.RefreshToken is null ? null : protector.Seal(grant.RefreshToken);

        try
        {
            if (connection is null)
            {
                if (sealedCredential is null)
                {
                    // Nothing held and nothing returned. Recording a connection here would mean a
                    // status of "connected" that 6b could never dispatch against.
                    logger.LogWarning("Calendar authorization returned no refresh token and none is held.");

                    return RedirectWithError(returnPath, ErrorCodes.CalendarConnectFailed);
                }

                database.CalendarConnections.Add(CalendarConnection.Establish(
                    professionalId,
                    CalendarProvider.Google,
                    calendar.TargetCalendarId,
                    sealedCredential,
                    now));
            }
            else
            {
                // Null means "keep what is held" — see the aggregate. Reconnecting updates the
                // one row rather than inserting a second (design K10).
                connection.Reconnect(sealedCredential, calendar.TargetCalendarId, now);
            }

            await SaveWithConsentAsync(database, actor.UserId(), authOptions.Value.ConsentVersion, now, cancellationToken);
        }
        catch (DomainRuleViolationException exception)
        {
            logger.LogWarning(exception, "Calendar connection could not be established.");

            return RedirectWithError(returnPath, ErrorCodes.CalendarConnectFailed);
        }

        return Results.Redirect(QueryHelpers.AddQueryString(returnPath, ConnectedQueryParameter, "1"));
    }

    /// <summary><c>GET /api/calendar/connection</c> — what S2 renders.</summary>
    private static async Task<IResult> ReadAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        if (await ProfessionalIdAsync(database, actor, cancellationToken) is not { } professionalId)
        {
            return CatalogRefusals.NotFound();
        }

        var connection = await database.CalendarConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProfessionalId == professionalId, cancellationToken);

        return Results.Ok(connection is null
            ? CalendarConnectionResponse.NeverConnected()
            : CalendarConnectionResponse.From(
                connection,
                await ActiveConsentAsync(database, actor.UserId(), cancellationToken)));
    }

    /// <summary>
    /// The professional's calendar consent, if they currently hold one.
    /// </summary>
    /// <remarks>
    /// S2 is the only surface that can show this. Consents are otherwise read through P7, which
    /// is a patient screen — so a professional had no way to see what they agreed to or when,
    /// which would have made <c>identity-session</c>'s widened "visible to the user they belong
    /// to" true on paper and false in the product.
    /// </remarks>
    private static async Task<Consent?> ActiveConsentAsync(
        ClinicDbContext database,
        Guid userId,
        CancellationToken cancellationToken) =>
        await database.Consents
            .AsNoTracking()
            .Where(consent => consent.UserId == userId
                && consent.Type == ConsentType.CalendarSync
                && consent.RevokedAtUtc == null)
            .OrderByDescending(consent => consent.GrantedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// <c>POST /api/calendar/connection/check</c> — asks Google whether the grant still stands.
    /// </summary>
    /// <remarks>
    /// Explicit rather than automatic, and the alternatives were both worse (design K8). Probing
    /// on every read ties a screen load to Google's availability; probing on a throttle hides a
    /// network call behind a page load and makes the validation guide wait out a timer to see the
    /// flip it exists to observe. A screen that says "checked four minutes ago" beside a button
    /// is more honest than one that silently decides how stale is acceptable.
    /// </remarks>
    private static async Task<IResult> CheckAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        GoogleCalendarTokens tokens,
        CalendarTokenProtector protector,
        TimeProvider clock,
        ILogger<CalendarMarker> logger,
        CancellationToken cancellationToken)
    {
        if (await ProfessionalIdAsync(database, actor, cancellationToken) is not { } professionalId)
        {
            return CatalogRefusals.NotFound();
        }

        var connection = await database.CalendarConnections
            .FirstOrDefaultAsync(candidate => candidate.ProfessionalId == professionalId, cancellationToken);

        if (connection?.SealedCredential is null)
        {
            // Nothing to check, and Google is not asked. Distinct from the read above, which
            // reports "not connected" as a successful state.
            return ApiError.Result(ErrorCodes.CalendarNotConnected, StatusCodes.Status422UnprocessableEntity);
        }

        string credential;

        try
        {
            credential = protector.Open(connection.SealedCredential);
        }
        catch (CalendarTokenProtectionException exception)
        {
            // Almost always the encryption key having changed. Reported as a connection needing
            // re-establishing rather than as a server error, because that is the true remedy and
            // the professional can act on it.
            logger.LogError(exception, "A stored calendar credential could not be opened.");

            connection.ObserveRevoked(clock.GetUtcNow());
            await database.SaveChangesAsync(cancellationToken);

            return ApiError.Result(ErrorCodes.CalendarConsentRevoked, StatusCodes.Status422UnprocessableEntity);
        }

        var outcome = await tokens.ProbeAsync(credential, cancellationToken);
        var now = clock.GetUtcNow();

        switch (outcome)
        {
            case CalendarProbeOutcome.Valid:
                connection.ObserveUsable(now);
                await database.SaveChangesAsync(cancellationToken);

                return Results.Ok(CalendarConnectionResponse.From(
                    connection,
                    await ActiveConsentAsync(database, actor.UserId(), cancellationToken)));

            case CalendarProbeOutcome.Revoked:
                connection.ObserveRevoked(now);
                await database.SaveChangesAsync(cancellationToken);

                return ApiError.Result(ErrorCodes.CalendarConsentRevoked, StatusCodes.Status422UnprocessableEntity);

            default:
                // Unreachable. The recorded state and its observation moment are deliberately
                // left alone: an outage is not evidence about the authorization, and recording it
                // as one would tell a professional to reconnect a connection that is fine.
                return ApiError.Result(ErrorCodes.CalendarSyncFailed, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// <c>POST /api/calendar/connection/disconnect</c> — the professional withdrawing their own.
    /// </summary>
    /// <remarks>
    /// The sequence lives in <see cref="CalendarWithdrawal"/> rather than here, because this is
    /// no longer the only caller: disabling and deactivating an account withdraw the same way
    /// (design K16). What stays here is what is specific to a professional doing it themselves —
    /// the refusal when there is nothing to withdraw, and reporting whether Google confirmed, so
    /// the screen can say the grant may still be listed in their account rather than claiming an
    /// unqualified success.
    /// </remarks>
    private static async Task<IResult> DisconnectAsync(
        ClaimsPrincipal actor,
        ClinicDbContext database,
        CalendarWithdrawal withdrawal,
        CancellationToken cancellationToken)
    {
        if (await ProfessionalIdAsync(database, actor, cancellationToken) is not { } professionalId)
        {
            return CatalogRefusals.NotFound();
        }

        var outcome = await withdrawal.WithdrawAsync(professionalId, actor.UserId(), cancellationToken);

        if (!outcome.HadConnection)
        {
            return ApiError.Result(ErrorCodes.CalendarNotConnected, StatusCodes.Status422UnprocessableEntity);
        }

        var connection = await database.CalendarConnections
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProfessionalId == professionalId, cancellationToken);

        return Results.Ok(new CalendarDisconnectResponse(
            CalendarConnectionResponse.From(connection),
            outcome.RevokedAtProvider));
    }

    /// <summary>
    /// Writes the calendar consent alongside the connection, in one transaction (design K12).
    /// </summary>
    /// <remarks>
    /// A connection without its consent record, or a consent for a connection that failed to
    /// save, are both states nobody should have to reason about — so neither is representable.
    /// Idempotent at the current version, for the same reason granting one twice from P3 is: a
    /// second press should not be a second legal fact.
    /// </remarks>
    private static async Task SaveWithConsentAsync(
        ClinicDbContext database,
        Guid userId,
        string version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var active = await database.Consents
            .AnyAsync(
                consent => consent.UserId == userId
                    && consent.Type == ConsentType.CalendarSync
                    && consent.RevokedAtUtc == null
                    && consent.Version == version,
                cancellationToken);

        if (!active)
        {
            database.Consents.Add(Consent.Grant(userId, ConsentType.CalendarSync, version, now));
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The caller's professional record, or null when they hold the role and have no clinical
    /// configuration yet.
    /// </summary>
    /// <remarks>
    /// A real state rather than an edge case (design E1), and refused the same way S3 refuses it:
    /// the catalogue's not-found, because the remedy is administrative and the state resolves
    /// itself once an administrator saves them in S7.
    /// </remarks>
    private static async Task<Guid?> ProfessionalIdAsync(
        ClinicDbContext database,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken) =>
        await database.Professionals
            .AsNoTracking()
            .Where(professional => professional.UserId == actor.UserId()
                && professional.DeactivatedAtUtc == null)
            .Select(professional => (Guid?)professional.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static IResult RedirectWithError(string returnPath, string errorCode) =>
        Results.Redirect(QueryHelpers.AddQueryString(returnPath, ErrorQueryParameter, errorCode));

    /// <summary>Anchor for the slice's logger category.</summary>
    private sealed class CalendarMarker;
}
