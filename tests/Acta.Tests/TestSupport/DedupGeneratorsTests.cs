using CsCheck;
using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Self-tests for <see cref="DedupGenerators"/> — confirm the generators actually produce the
/// shapes the dedup property tests rely on, so a broken generator fails here rather than silently
/// weakening a property (e.g. an intra-duplicate batch that happens to carry no duplicate, or an
/// "overlapping" retry that shares no key or carries no fresh key).
/// </summary>
public sealed class DedupGeneratorsTests
{
    [Fact]
    public void DistinctBatch_HasNoRepeatedEventId()
        => DedupGenerators.DistinctBatch.Sample(batch =>
            batch.Length == batch.Select(e => e.EventId).Distinct().Count());

    [Fact]
    public void BatchWithIntraDuplicate_ContainsAtLeastOneRepeatedEventId()
        => DedupGenerators.BatchWithIntraDuplicate.Sample(batch =>
            batch.Length >= 2 && batch.Select(e => e.EventId).Distinct().Count() < batch.Length);

    [Fact]
    public void PartiallyOverlappingBatches_ShareAKeyAndCarryAFreshKey()
        => DedupGenerators.PartiallyOverlappingBatches.Sample(pair =>
        {
            var (first, partialRetry) = pair;
            var firstIds = first.Select(e => e.EventId).ToHashSet();
            var retryIds = partialRetry.Select(e => e.EventId).ToHashSet();

            var sharesKey = retryIds.Overlaps(firstIds);
            var hasFreshKey = retryIds.Except(firstIds).Any();
            return sharesKey && hasFreshKey;
        });

    [Fact]
    public void ExpectedVersionMode_OnlyYieldsKnownModes()
    {
        long[] known = [ExpectedVersion.Any, ExpectedVersion.NoStream, ExpectedVersion.EmptyStream,
            ExpectedVersion.StreamExists, 1L, 5L, 999L];

        DedupGenerators.ExpectedVersionMode.Sample(mode => known.Contains(mode));
    }
}
