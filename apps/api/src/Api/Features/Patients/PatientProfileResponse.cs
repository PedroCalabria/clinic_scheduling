using System.Text.Json.Serialization;
using Clinic.Domain.Identity;

namespace Clinic.Api.Features.Patients;

/// <summary>
/// What P7 renders: the patient's own minimal data and the state of their consents.
/// </summary>
/// <remarks>
/// Consents are returned with their grant and revocation moments rather than a bare boolean,
/// because "granted on the 3rd, withdrawn on the 9th" is the fact the record exists to keep
/// (02-domain-model.md §LGPD). The UI decides how much of that to show.
/// </remarks>
internal sealed record PatientProfileResponse(
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("contactEmail")] string ContactEmail,
    [property: JsonPropertyName("contactPhone")] string? ContactPhone,
    [property: JsonPropertyName("consents")] IReadOnlyList<ConsentResponse> Consents);

internal sealed record ConsentResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("grantedAtUtc")] DateTimeOffset GrantedAtUtc,
    [property: JsonPropertyName("revokedAtUtc")] DateTimeOffset? RevokedAtUtc,
    [property: JsonPropertyName("active")] bool Active)
{
    internal static ConsentResponse From(Consent consent) =>
        new(consent.Type.ToString(), consent.Version, consent.GrantedAtUtc, consent.RevokedAtUtc, consent.IsActive);
}
