using System.Text.Json.Serialization;
using System.Security.Claims;
using Clinic.Api.Infrastructure.Auth;

namespace Clinic.Api.Features.Auth;

/// <summary>
/// What the frontend is told about the current session — the single source of truth both
/// apps read (design A11).
/// </summary>
/// <remarks>
/// Deliberately thin. It carries what the UI has to branch on (which navigation to show,
/// whether to force the password screen) and nothing that authorization depends on: the
/// server decides every permission from the session row, so nothing here is worth forging.
///
/// The user's id is not exposed. The frontend never needs it — it asks for "my profile", not
/// for a profile by id — and leaving it out means no client code can start building requests
/// around an identifier that ownership checks would refuse anyway.
/// </remarks>
internal sealed record SessionResponse(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("mustChangePassword")] bool MustChangePassword)
{
    internal static SessionResponse From(ClaimsPrincipal principal, string email) =>
        new(email, principal.Role().ToString(), principal.MustChangePassword());
}
