using Clinic.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// The professional-scoped transaction lock itself (domain-model G1, design B5 and B7).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the most important assertion in <c>booking-core</c>, and the reason it exists is
/// that its failure mode is invisible.</b> A lock taken on a connection other than the one the
/// transaction runs on is released immediately and protects nothing — and every functional test
/// in the repository still passes, because a test that does not race never notices. So the lock
/// is asserted here, directly, before anything is built on top of it: the booking-versus-block
/// serialization test later is a test of the retrofit, not of the mechanism.
/// </para>
/// <para>
/// Written against arbitrary ids rather than real professionals. The lock keys on a Guid and
/// knows nothing about rows, so seeding a clinic here would only add ways for the test to fail
/// for reasons that are not the lock.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ScheduleLockTests(ApiFixture fixture)
{
    /// <summary>
    /// Long enough that a non-blocking acquire would certainly have completed, short enough not
    /// to slow the suite. Only reached on the pass path, where the wait is the assertion.
    /// </summary>
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task A_second_transaction_blocks_while_the_first_holds_one_professionals_lock()
    {
        var professionalId = Guid.NewGuid();

        await using var holderScope = fixture.CreateScope();
        await using var waiterScope = fixture.CreateScope();

        var holderDb = holderScope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var waiterDb = waiterScope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        var acquired = new TaskCompletionSource();
        var mayRelease = new TaskCompletionSource();

        var holder = Task.Run(async () =>
        {
            await using var transaction = await holderDb.Database.BeginTransactionAsync();
            await ScheduleMutation.TakeProfessionalLockAsync(holderDb, professionalId, CancellationToken.None);

            acquired.SetResult();
            await mayRelease.Task;

            // Rolled back rather than committed: the point being proved is that the lock is
            // transaction-scoped, so it must be released by a transaction that ENDS, not by one
            // that succeeds.
            await transaction.RollbackAsync(CancellationToken.None);
        });

        await acquired.Task;

        await using var waiterTransaction = await waiterDb.Database.BeginTransactionAsync();
        var waiting = ScheduleMutation.TakeProfessionalLockAsync(waiterDb, professionalId, CancellationToken.None);

        var raced = await Task.WhenAny(waiting, Task.Delay(BlockedFor));

        // If the second acquire completed, the lock is not doing its job — most likely because
        // the SQL ran on a different connection than the transaction, which is design B5's
        // whole warning.
        Assert.NotSame(waiting, raced);

        mayRelease.SetResult();
        await holder;

        // And it acquires once the first transaction ends, so this is a lock rather than a
        // deadlock.
        await waiting;
        await waiterTransaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Two_different_professionals_do_not_block_each_other()
    {
        await using var firstScope = fixture.CreateScope();
        await using var secondScope = fixture.CreateScope();

        var firstDb = firstScope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync();
        await ScheduleMutation.TakeProfessionalLockAsync(firstDb, Guid.NewGuid(), CancellationToken.None);

        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync();

        // The lock is scoped to one professional's busy set, which is the entire reason it was
        // chosen over SERIALIZABLE: two unrelated bookings must not queue behind each other.
        await ScheduleMutation.TakeProfessionalLockAsync(secondDb, Guid.NewGuid(), CancellationToken.None);

        await firstTransaction.RollbackAsync(CancellationToken.None);
        await secondTransaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_handler_releases_the_lock()
    {
        var professionalId = Guid.NewGuid();

        await using var firstScope = fixture.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        // A handler that takes the lock and then throws. Nothing unlocks explicitly; the
        // transaction ending is what has to do it (design B7 — the reason the lock is
        // xact-scoped rather than session-scoped).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var transaction = await firstDb.Database.BeginTransactionAsync();
            await ScheduleMutation.TakeProfessionalLockAsync(firstDb, professionalId, CancellationToken.None);

            throw new InvalidOperationException("simulated handler failure");
        });

        await using var secondScope = fixture.CreateScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync();

        var acquire = ScheduleMutation.TakeProfessionalLockAsync(secondDb, professionalId, CancellationToken.None);
        var raced = await Task.WhenAny(acquire, Task.Delay(BlockedFor));

        Assert.Same(acquire, raced);
        await acquire;

        await secondTransaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Taking_the_lock_outside_a_transaction_is_refused_rather_than_silently_useless()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        // The guard that turns design B5's invisible bug into a loud one. Without a transaction
        // a transaction-scoped lock is released the instant the statement returns, so a caller
        // who forgot has written a race that looks like working code.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScheduleMutation.TakeProfessionalLockAsync(database, Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("transaction", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_lock_key_is_stable_across_calls_and_folds_both_halves_of_the_id()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // Stable, because the key must be the same in the booking path and the block path and
        // across process restarts — a lock keyed differently in two places is not a lock.
        Assert.Equal(ScheduleMutation.LockKey(id), ScheduleMutation.LockKey(id));

        // Both halves participate, so two ids differing only in their tail are distinguished.
        // Sequential Guid generators can leave one half nearly constant, which is why the
        // derivation folds rather than truncating.
        var differsInTail = Guid.Parse("11111111-2222-3333-4444-555555555556");

        Assert.NotEqual(ScheduleMutation.LockKey(id), ScheduleMutation.LockKey(differsInTail));
    }
}
