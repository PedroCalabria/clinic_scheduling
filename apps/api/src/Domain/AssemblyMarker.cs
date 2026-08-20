namespace Clinic.Domain;

/// <summary>
/// Anchor type for referencing the Domain assembly (tests, future DI scanning).
/// </summary>
/// <remarks>
/// The protected core is deliberately empty in change 1 (walking-skeleton): this change
/// builds structure only. The Appointment aggregate, invariants I1-I10, the state machine,
/// and the availability-solver contracts land in changes 4-5.
/// What exists now is the boundary — see Domain.csproj.
/// </remarks>
public static class DomainAssemblyMarker;
