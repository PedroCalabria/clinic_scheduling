using System.Threading.RateLimiting;
using Clinic.Api.Infrastructure.Time;
using Microsoft.AspNetCore.RateLimiting;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// The brake on the availability search — the second use of Decision R's native middleware,
/// anticipated by <see cref="LoginRateLimiting"/> and shaped now that the query exists.
/// </summary>
/// <remarks>
/// <para>
/// A separate policy rather than a wider login one, because they defend different things. The
/// login limiter answers "many accounts tried from one place" and is deliberately tight. This one
/// bounds query cost on the endpoint 03-nfr.md §2 names as the abusable surface, and its budget is
/// a normal person clicking through a booking calendar rather than a credential guess.
/// </para>
/// <para>
/// <b>Partitioned by caller, not by address.</b> The login limiter uses the address because it has
/// no authenticated caller to key on; this endpoint always does. Keying by address would put a
/// whole clinic behind one NAT into a single bucket, so the front desk's ordinary work would
/// throttle itself. The address remains the fallback for a request that somehow arrives
/// unauthenticated, which the authorization layer refuses anyway.
/// </para>
/// <para>
/// The rejection response is deliberately <em>not</em> configured here. <c>OnRejected</c> is set
/// once, by <see cref="LoginRateLimiting"/>, and writes the <c>auth.rate_limited</c> envelope with
/// its retry hint — which is the right answer for this endpoint too. One code, because the failure
/// is the same one the caller sees and the same remedy: wait. 07-error-codes.md's rule is one code
/// per user-meaningful failure, not one per throw site.
/// </para>
/// </remarks>
internal static class AvailabilityRateLimiting
{
    internal const string PolicyName = "availability";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal static IServiceCollection AddAvailabilityRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
            limiter.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolveCallerKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices
                        .GetRequiredService<ClinicScheduling>().AvailabilityRequestsPerMinute,
                    Window = Window,

                    // No queue: a caller past their budget should be told now, not held open. A
                    // queued availability request would also be answering a question about a
                    // moment that has since passed.
                    QueueLimit = 0,
                })));

    private static string ResolveCallerKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return $"user:{context.User.UserId()}";
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return $"ip:{forwarded.Split(',')[0].Trim()}";
        }

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
