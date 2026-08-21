namespace Clinic.Api.Features.Auth;

/// <summary>
/// Maps the auth slice, so Program.cs names one thing per feature area rather than one thing
/// per endpoint (the health slice's pattern, scaled up).
/// </summary>
internal static class AuthEndpoints
{
    internal static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Both sign-in paths carry the login rate limit; nothing else does (design A10).
        endpoints.MapSignIn().RequireRateLimiting(Infrastructure.Auth.LoginRateLimiting.PolicyName);
        endpoints.MapSignOut();
        endpoints.MapGetCurrentSession();
        endpoints.MapChangePassword();

        // The Google endpoints carry the same limiter: the callback is as reachable as the
        // sign-in form, and both are anonymous (design A10).
        endpoints.MapStartGoogleSignIn().RequireRateLimiting(Infrastructure.Auth.LoginRateLimiting.PolicyName);
        endpoints.MapCompleteGoogleSignIn().RequireRateLimiting(Infrastructure.Auth.LoginRateLimiting.PolicyName);

        return endpoints;
    }
}
