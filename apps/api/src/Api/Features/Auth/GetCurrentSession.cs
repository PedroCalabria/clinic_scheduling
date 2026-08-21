using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;
using Clinic.Api.Infrastructure.Errors;
using Clinic.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// <c>GET /api/auth/session</c> — who the caller is, as far as the server is concerned.
/// </summary>
/// <remarks>
/// This endpoint exists so the frontend never keeps its own copy of the session (design A11).
/// Both apps read it on boot and after any change, and a <c>401</c> here is not an error —
/// it is the answer "you are signed out", which is exactly what a route guard needs to know.
///
/// It is authenticated (by the default fallback policy), so the <c>401</c> comes from the
/// authentication handler with the catalogue code in the body rather than an empty response.
/// </remarks>
internal static class GetCurrentSession
{
    internal static RouteHandlerBuilder MapGetCurrentSession(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/auth/session", HandleAsync)
            .WithName("GetCurrentSession");

    private static async Task<IResult> HandleAsync(
        ClaimsPrincipal principal,
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();

        // The email is read fresh rather than carried as a claim: claims are a snapshot taken
        // when the session was issued, and the row is the authority (design A1).
        var email = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (email is null)
        {
            // The session resolved but the user is gone — treat it as signed out rather than
            // as a server error.
            return ApiError.Result(ErrorCodes.SessionExpired, StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(SessionResponse.From(principal, email));
    }
}
