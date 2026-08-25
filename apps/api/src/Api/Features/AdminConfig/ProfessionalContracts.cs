namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// S7's request and response shapes.
/// </summary>
/// <remarks>
/// Everything is keyed by <c>userId</c>, not by a professional id, and that is design E1 showing
/// through the API: S7 lists users with the professional role, and the configuration record may
/// not exist yet. A caller should not have to know whether it does.
///
/// Times cross the wire as <c>"HH:mm"</c> and dates as <c>"yyyy-MM-dd"</c> — plain wall-clock
/// strings with no offset and no <c>Z</c>. An ISO instant here would invite a client to attach a
/// timezone, which is the one thing this data must never carry (design E3).
/// </remarks>
internal sealed record ProfessionalListEntry(
    Guid UserId,
    string Email,
    /// <summary>
    /// The stored name, or null while nobody has entered one (P-5, design N10). Null is an
    /// ordinary state, not an error: an invited professional has no configuration record to hold
    /// a name on, and S7 lists them regardless.
    /// </summary>
    string? FullName,
    /// <summary>False for an invited professional nobody has configured yet.</summary>
    bool IsConfigured,
    /// <summary>True while the invitation has not been claimed by a first sign-in.</summary>
    bool AwaitsClaim,
    bool IsActive,
    int SpecialtyCount,
    int DurationCount,
    int WorkingHoursCount);

internal sealed record HeldSpecialty(Guid SpecialtyId, string SpecialtyName);

internal sealed record ConfiguredDuration(
    Guid AppointmentTypeId,
    string AppointmentTypeName,
    Guid SpecialtyId,
    string SpecialtyName,
    int DurationMinutes);

internal sealed record WorkingHoursSegment(
    Guid Id,
    string DayOfWeek,
    string StartTime,
    string EndTime,
    string EffectiveFrom,
    string? EffectiveTo);

internal sealed record WorkingHoursOverride(
    Guid Id,
    string Date,
    /// <summary>Null on both when the professional is unavailable for the whole day.</summary>
    string? StartTime,
    string? EndTime);

/// <summary>Everything S7's detail view needs, in one read.</summary>
internal sealed record ProfessionalDetail(
    Guid UserId,
    string Email,
    string? FullName,
    bool IsConfigured,
    bool AwaitsClaim,
    IReadOnlyList<HeldSpecialty> Specialties,
    IReadOnlyList<ConfiguredDuration> Durations,
    IReadOnlyList<WorkingHoursSegment> WorkingHours,
    IReadOnlyList<WorkingHoursOverride> Exceptions);

/// <summary>
/// Naming a professional (P-5).
/// </summary>
/// <remarks>
/// Nullable, and an empty submission clears the name rather than storing whitespace — the same
/// shape P7 uses for a patient's contact phone. Clearing it restores the derived label rather than
/// leaving a blank where a name should be, which is why removing a name is a safe act.
/// </remarks>
internal sealed record RenameProfessionalRequest(string? FullName);

internal sealed record GrantSpecialtyRequest(Guid SpecialtyId);

internal sealed record SetDurationRequest(Guid AppointmentTypeId, int DurationMinutes);

internal sealed record DefineWorkingHoursRequest(
    string? DayOfWeek,
    string? StartTime,
    string? EndTime,
    string? EffectiveFrom,
    string? EffectiveTo);

internal sealed record DefineExceptionRequest(string? Date, string? StartTime, string? EndTime);
