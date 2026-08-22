namespace Clinic.Api.Features.AdminConfig;

/// <summary>
/// The catalog's request and response shapes (S8-S10).
/// </summary>
/// <remarks>
/// <c>IsActive</c> is on every response rather than the raw <c>DeactivatedAtUtc</c>: the screen
/// needs to know whether a record is offered, not when it stopped being offered, and shipping
/// the timestamp would invite a frontend to render a date nobody asked for.
/// </remarks>
internal sealed record CatalogNameRequest(string? Name);

internal sealed record SpecialtyResponse(Guid Id, string Name, bool IsActive);

internal sealed record ResourceTypeRequest(string? Name, int BufferMinutes);

internal sealed record ResourceTypeResponse(Guid Id, string Name, int BufferMinutes, bool IsActive);

internal sealed record ResourceRequest(string? Name, Guid ResourceTypeId);

/// <summary>
/// A resource with its type's name resolved, so the table can be read without a second call.
/// </summary>
internal sealed record ResourceResponse(
    Guid Id,
    string Name,
    Guid ResourceTypeId,
    string ResourceTypeName,
    bool IsActive);

internal sealed record AppointmentTypeRequest(string? Name, Guid SpecialtyId, Guid RequiredResourceTypeId);

/// <summary>
/// A kind of visit with both references resolved.
/// </summary>
/// <remarks>
/// Deliberately carries no duration — Decision C keeps that on change 3b's
/// professional × type junction, and a field here would misrepresent a per-professional value
/// as a clinic-wide default. A unit test asserts the entity has no such property.
/// </remarks>
internal sealed record AppointmentTypeResponse(
    Guid Id,
    string Name,
    Guid SpecialtyId,
    string SpecialtyName,
    Guid RequiredResourceTypeId,
    string RequiredResourceTypeName,
    bool IsActive);
