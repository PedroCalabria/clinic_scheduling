using Clinic.Api.Infrastructure.Errors;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// Holds an account that is still using its bootstrap credential to the one thing it has to
/// do next: replace it (design A6).
/// </summary>
/// <remarks>
/// <para>
/// The forced change is the primary control that stops a known-password administrator from
/// quietly becoming permanent; the startup warning is only the backstop. A control that can
/// be walked past by navigating somewhere else is not a control, so it is enforced here in
/// the pipeline rather than by the screen that shows the form.
/// </para>
/// <para>
/// The allow-list is short and explicit: read your session, change your password, sign out.
/// Anything else is refused with a distinct code so the frontend can route to the
/// change-password screen instead of showing a generic refusal.
/// </para>
/// </remarks>
internal sealed class PasswordChangeGate(RequestDelegate next)
{
    /// <summary>Requests a user in this state may still make.</summary>
    private static readonly string[] PermittedPaths =
    [
        "/api/auth/password",
        "/api/auth/sign-out",
        "/api/auth/session",
        "/api/health",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true || !context.User.MustChangePassword())
        {
            await next(context);
            return;
        }

        var path = context.Request.Path;

        if (PermittedPaths.Any(permitted => path.StartsWithSegments(permitted, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        await ApiError.WriteAsync(
            context.Response,
            ErrorCodes.PasswordChangeRequired,
            StatusCodes.Status403Forbidden,
            cancellationToken: context.RequestAborted);
    }
}
