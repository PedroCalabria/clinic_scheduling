namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// Minimal marker row whose only purpose is to give change 1 a real table, so the
/// EF Core migration path is proven end to end against PostgreSQL before any real
/// schema exists. The clinic schema arrives in change 3 (clinic-configuration).
/// </summary>
internal sealed class PlatformMarker
{
    public Guid Id { get; init; }

    /// <summary>UTC, per the time convention (00-context.md §5 / Decision H).</summary>
    public DateTimeOffset RecordedAtUtc { get; init; }

    public required string Description { get; init; }
}
