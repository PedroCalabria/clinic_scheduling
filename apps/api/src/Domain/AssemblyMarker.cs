namespace Clinic.Domain;

/// <summary>
/// Anchor type for referencing the Domain assembly (tests, future DI scanning).
/// </summary>
/// <remarks>
/// Change 1 (walking-skeleton) established the boundary with nothing behind it — see
/// Domain.csproj. Change 2 (identity-session) put the first rules in it: the identity
/// entities, the two attributes that cannot change after creation (design A5), and the
/// patient-data ownership rule. The Appointment aggregate, invariants I1-I10, the state
/// machine, and the availability-solver contracts land in changes 4-5.
/// </remarks>
public static class DomainAssemblyMarker;
