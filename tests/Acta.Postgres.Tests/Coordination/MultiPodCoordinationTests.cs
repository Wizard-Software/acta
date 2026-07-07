using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Tests.Infrastructure;

using Xunit;

namespace Acta.Postgres.Tests.Coordination;

/// <summary>
/// The ≥2-pod coordination harness (TESTING-SPEC §5.2, task 7.6) on a real PostgreSQL: two "pods"
/// (separate backend sessions in one test process, modelled by <see cref="Pod"/>) contend for the same
/// projection slot, and we assert the three BASELINE multi-pod behaviours (ADR-014, D14 — baseline, not
/// an extension): <b>election</b> (exactly one leader), <b>fencing</b> (a zombie leader's write is
/// rejected), and <b>failover</b> (a peer takes over from the last checkpoint after the leader's session
/// dies). Through these we exercise daemon-algorithm properties (1)–(3) of 05-implementation §3:
/// <list type="number">
///   <item>(1) a projection never rolls its checkpoint backward — asserted under both a zombie write and
///     a same-owner backward write across failover;</item>
///   <item>(2) two pods never write the same projection's checkpoint concurrently — guaranteed by
///     mutually-exclusive leadership (at most one live leader) plus <c>owner_token</c> fencing;</item>
///   <item>(3) the failover window yields at-most repeated Apply (at-least-once) — the taking-over pod
///     resumes from the crashed leader's last durable checkpoint, never skipping ahead of it.</item>
/// </list>
/// Property (4) ("a gap skip always leaves a diagnostic trace") is a <c>GapGuard</c> concern (in-memory,
/// tasks 5.3/5.5) with no multi-pod semantics, so it is intentionally not re-tested at the pod level here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MultiPodCoordinationTests(PostgresFixture fixture)
{
    private const string Projection = "proj-1";

    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Pod NewPod(ActaPostgresOptions options, string ownerToken)
        => new(_fixture, options, ownerToken);

    private Task<ActaPostgresOptions> FreshMigratedSchemaAsync()
        => PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct).AsTask();

    // --- Election ---------------------------------------------------------------------------------

    [Fact]
    public async Task TwoPods_ContendForSlot_ExactlyOneBecomesLeader()
    {
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        var aWon = await podA.TryAcquireLeadershipAsync(Projection, null, Ct);
        var bWon = await podB.TryAcquireLeadershipAsync(Projection, null, Ct);

        // Election: exactly one of the two contenders owns the slot; the other is told "not leader".
        aWon.Should().BeTrue();
        bWon.Should().BeFalse();
        podA.IsLeader.Should().BeTrue();
        podB.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task Leadership_IsMutuallyExclusive_WhileHeld()
    {
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        (await podA.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeTrue();

        // Property (2) precondition: while A holds the slot, B can never also hold it — repeated attempts
        // keep failing, so the two pods can never be leaders (hence writers) of the same projection at once.
        (await podB.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeFalse();
        (await podB.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeFalse();

        // Only after A cleanly releases does B win the slot.
        await podA.DisposeAsync();
        (await podB.AcquireLeadershipWithFailoverAsync(Projection, null, Ct)).Should().BeTrue();
    }

    // --- Failover ---------------------------------------------------------------------------------

    [Fact]
    public async Task LeaderCrash_ReleasesLeadership_EnablingFailover()
    {
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        (await podA.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeTrue();
        (await podA.IsLeadershipHeldAsync(Ct)).Should().BeTrue();

        // Abrupt node death (session loss, not a clean release) frees the advisory lock in PostgreSQL.
        await podA.KillLeadershipConnectionAsync(Ct);
        (await podA.IsLeadershipHeldAsync(Ct)).Should().BeFalse();

        // Failover: B takes leadership over the released slot.
        (await podB.AcquireLeadershipWithFailoverAsync(Projection, null, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Failover_SecondPodResumesFromLastCheckpoint_AtLeastOnce()
    {
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        // A leads and durably checkpoints position 50, then crashes before advancing further.
        (await podA.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeTrue();
        await podA.SaveCheckpointAsync(Projection, null, 50, Ct);
        await podA.KillLeadershipConnectionAsync(Ct);

        // B fails over and resumes from A's last durable checkpoint — position 50, never a skipped-ahead
        // value. Reprocessing from 50 is the at-least-once (property 3) window: safe under idempotent Apply.
        (await podB.AcquireLeadershipWithFailoverAsync(Projection, null, Ct)).Should().BeTrue();
        (await podB.LoadCheckpointAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(50));

        // B then advances the same projection under its own token (takeover write applies).
        await podB.SaveCheckpointAsync(Projection, null, 100, Ct);
        (await podB.LoadCheckpointAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(100));
    }

    // --- Fencing ----------------------------------------------------------------------------------

    [Fact]
    public async Task ZombieLeader_AfterFailover_CheckpointWriteIsFenced()
    {
        // The canonical TESTING-SPEC §5.2 scenario, end-to-end across election + failover + fencing.
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        (await podA.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeTrue();
        await podA.SaveCheckpointAsync(Projection, null, 50, Ct);

        // A's session dies; B fails over and advances strictly ahead (§3.3 takeover grant).
        await podA.KillLeadershipConnectionAsync(Ct);
        (await podB.AcquireLeadershipWithFailoverAsync(Projection, null, Ct)).Should().BeTrue();
        await podB.SaveCheckpointAsync(Projection, null, 100, Ct);

        // A is now a zombie (lost leadership but still tries to save its stale position); its
        // non-advancing write matches zero rows and is turned into CheckpointFencedException.
        var fenced = (await Awaiting(() => podA.SaveCheckpointAsync(Projection, null, 90, Ct).AsTask())
            .Should().ThrowAsync<CheckpointFencedException>()).Which;
        fenced.ProjectionName.Should().Be(Projection);
        fenced.OwnerToken.Should().Be(podA.OwnerToken);

        // The zombie write left the checkpoint untouched at B's advanced position.
        (await podB.LoadCheckpointAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(100));
    }

    [Fact]
    public async Task Checkpoint_NeverMovesBackward_UnderFailoverAndZombieWrites()
    {
        // Property (1): the checkpoint only advances — neither a zombie write nor a same-owner backward
        // write can roll it back, across a failover.
        var options = await FreshMigratedSchemaAsync();
        await using var podA = NewPod(options, "owner-a");
        await using var podB = NewPod(options, "owner-b");

        (await podA.TryAcquireLeadershipAsync(Projection, null, Ct)).Should().BeTrue();
        await podA.SaveCheckpointAsync(Projection, null, 100, Ct);
        await podA.KillLeadershipConnectionAsync(Ct);

        // B fails over, resumes from 100, and advances strictly ahead to 150 — the §3.3 grant
        // (position < @new) transfers the owner_token to B, so A is now a genuine zombie.
        (await podB.AcquireLeadershipWithFailoverAsync(Projection, null, Ct)).Should().BeTrue();
        (await podB.LoadCheckpointAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(100));
        await podB.SaveCheckpointAsync(Projection, null, 150, Ct);

        // Zombie A tries to move it back to 40 → fenced (different, non-null owner B holds the row).
        await Awaiting(() => podA.SaveCheckpointAsync(Projection, null, 40, Ct).AsTask())
            .Should().ThrowAsync<CheckpointFencedException>();

        // Legit leader B tries to roll its own checkpoint back to 60 → forbidden (advance-only guard).
        await Awaiting(() => podB.SaveCheckpointAsync(Projection, null, 60, Ct).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        // After every rejected backward attempt the durable checkpoint still stands at its high-water mark.
        (await podB.LoadCheckpointAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(150));
    }
}
