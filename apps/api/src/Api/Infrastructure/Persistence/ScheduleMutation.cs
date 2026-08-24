using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Clinic.Api.Infrastructure.Persistence;

/// <summary>
/// The two things every schedule-mutating path needs: raw SQL on the context's own connection,
/// and the professional-scoped lock (domain-model G1, design B5 and B7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is one file rather than two helpers where they are used.</b> Both paths that
/// mutate a professional's schedule — booking an appointment, and creating or moving an internal
/// block — must take the same lock, keyed the same way, before the reads it protects. Two copies
/// of a key derivation is two chances to key them differently, at which point the lock is
/// present, passing, and protecting nothing.
/// </para>
/// </remarks>
internal static class ScheduleMutation
{
    /// <summary>
    /// The context's own open connection, ready for Dapper, and the ambient transaction it must
    /// run inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the load-bearing detail of design B5, and its failure mode is invisible.</b> A
    /// transaction-scoped advisory lock taken on a <em>different</em> connection is taken in a
    /// different transaction, released the moment that connection is returned to the pool, and
    /// protects nothing at all — while every functional test still passes, because a test that
    /// does not race never notices. The same is true of the overlap reads: on another connection
    /// they see another snapshot, so the busy set the domain is handed is not the one the insert
    /// will be committed against.
    /// </para>
    /// <para>
    /// So the ambient transaction is not optional here, and its absence throws rather than being
    /// tolerated. A caller who has not begun one has written a race, not a slow path, and the
    /// exception says so at the moment it can still be fixed.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction has been started.</exception>
    internal static async Task<(DbConnection Connection, DbTransaction Transaction)> EnlistAsync(
        ClinicDbContext database,
        CancellationToken cancellationToken)
    {
        var transaction = database.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Schedule mutations must run inside a transaction started on the ClinicDbContext. "
                + "Raw SQL on another connection would take its advisory lock in another "
                + "transaction and read another snapshot — see design B5.");

        var connection = database.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            // Practically unreachable: beginning a transaction opens the connection. Kept so
            // the helper is correct rather than merely correct-in-context.
            await connection.OpenAsync(cancellationToken);
        }

        return (connection, transaction.GetDbTransaction());
    }

    /// <summary>
    /// Serializes this professional's schedule against the other mutating path, for the rest of
    /// the transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Take this before the reads it protects</b>, not around the write. The race being closed
    /// is read-then-write across two tables — booking reads blocks, block creation reads
    /// appointments — so a lock acquired after those reads serializes nothing whatsoever.
    /// </para>
    /// <para>
    /// Transaction-scoped (<c>pg_advisory_xact_lock</c>) rather than session-scoped, so it is
    /// released by commit or rollback and a handler that throws cannot leak it. Session-scoped
    /// locks need an explicit unlock, which is the kind of thing that survives the happy path and
    /// not the exception path.
    /// </para>
    /// <para>
    /// What this lock is <em>not</em> for: appointment-to-appointment overlap. The three
    /// exclusion constraints enforce that, for the resource and the patient as well as the
    /// professional, and no professional-scoped lock could cover the other two. Chosen over
    /// <c>SERIALIZABLE</c> because booking is frequent and block creation is rare, so this
    /// serializes only the two paths touching one professional's busy set instead of imposing
    /// serialization-failure retries on the hot path (domain-model G1).
    /// </para>
    /// </remarks>
    internal static async Task TakeProfessionalLockAsync(
        ClinicDbContext database,
        Guid professionalId,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = await EnlistAsync(database, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)",
            new { key = LockKey(professionalId) },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The advisory-lock key for one professional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL advisory locks are keyed by <c>bigint</c>, so 128 bits of identity have to
    /// become 64. <b>Collisions are therefore possible and are harmless:</b> two unrelated
    /// professionals sharing a key serialize against each other unnecessarily, which costs a
    /// little concurrency and cannot cost correctness — this lock is not what makes any check
    /// true, it only stops two paths interleaving between a read and a write.
    /// </para>
    /// <para>
    /// Folding both halves of the id rather than taking the first eight bytes, because a
    /// sequential or partly-structured Guid generator can leave one half nearly constant.
    /// </para>
    /// </remarks>
    internal static long LockKey(Guid professionalId)
    {
        Span<byte> bytes = stackalloc byte[16];
        professionalId.TryWriteBytes(bytes);

        var high = BitConverter.ToInt64(bytes[..8]);
        var low = BitConverter.ToInt64(bytes[8..]);

        return high ^ low;
    }
}
