namespace Clinic.Api.Features.Patients;

/// <summary>Maps the patient slice (P7, plus the staff read that <c>AccessLog</c> records).</summary>
internal static class PatientEndpoints
{
    internal static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // No role policy on these: the ownership rule decides, and it admits both a patient
        // reaching their own record and staff reaching anyone's. A role policy here would
        // have to name both and would then be the second place the rule lives (design A8).
        endpoints.MapGetPatientProfile();
        endpoints.MapUpdatePatientProfile();
        endpoints.MapRevokeConsent();

        // Added by booking-core: revoking was one-way until a consent became load-bearing
        // (design B12). P3 offers the grant in place so a refusal is recoverable where it happened.
        endpoints.MapGrantConsent();

        return endpoints;
    }
}
