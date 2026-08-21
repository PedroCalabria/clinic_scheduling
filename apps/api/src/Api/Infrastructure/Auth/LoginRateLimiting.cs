using System.Globalization;
using System.Threading.RateLimiting;
using Clinic.Api.Infrastructure.Errors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Auth;

/// <summary>
/// The brute-force brake on the login endpoints — Decision R's native middleware, in its
/// first real use (design A10).
/// </summary>
/// <remarks>
/// <para>
/// This is one of two independent mechanisms, and they defend different attacks. A limiter
/// partitioned by client address answers "many accounts tried from one place"; the
/// per-account failed-attempt lockout in <c>User</c> answers "one account tried from many
/// places". Either alone leaves the other attack unaddressed.
/// </para>
/// <para>
/// In-process is sufficient because the deployment is a single instance
/// (04-architecture.md §7). Distributed limiting solves a multi-instance coordination
/// problem this project does not have; horizontal scale is the documented trigger for
/// revisiting it, alongside Redis.
/// </para>
/// <para>
/// Scope is the login endpoints only. The public availability search gets its own limiter in
/// change 4, where its shape can be argued against real query cost rather than guessed at now.
/// </para>
/// </remarks>
internal static class LoginRateLimiting
{
    internal const string PolicyName = "login";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal static IServiceCollection AddLoginRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                // The partition key is the caller's address. Behind Caddy every request would
                // otherwise share one bucket, so the forwarded address is preferred when the
                // proxy supplies it — and falls back to the socket address, which is correct
                // for direct calls and in tests.
                partitionKey: ResolveClientKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices
                        .GetRequiredService<IOptions<AuthOptions>>().Value.LoginAttemptsPerMinute,
                    Window = Window,
                    // No queue: a login attempt that has to wait is an attempt to refuse, not
                    // to delay.
                    QueueLimit = 0,
                }));

            // Without this the middleware answers an empty 429, leaving the frontend nothing
            // to translate (Decision I). The retry hint is included because a client that
            // knows when to try again does not have to poll.
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var parameters = new Dictionary<string, object?>();

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                    parameters["retryAfterSeconds"] = seconds;

                    // Set through OnStarting, not directly: writing the envelope clears the
                    // response first (so a half-written body cannot leak), which would take this
                    // header with it. OnStarting runs after that clear, which is the same reason
                    // CorrelationIdMiddleware uses it.
                    var value = seconds.ToString(CultureInfo.InvariantCulture);

                    context.HttpContext.Response.OnStarting(state =>
                    {
                        ((HttpResponse)state).Headers.RetryAfter = value;
                        return Task.CompletedTask;
                    }, context.HttpContext.Response);
                }

                await ApiError.WriteAsync(
                    context.HttpContext.Response,
                    ErrorCodes.RateLimited,
                    StatusCodes.Status429TooManyRequests,
                    parameters.Count == 0 ? null : parameters,
                    cancellationToken);
            };
        });

    private static string ResolveClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            // Left-most entry is the original client; the rest are proxies.
            return forwarded.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
