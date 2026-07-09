using System.Collections.Concurrent;
using System.Diagnostics;

using Acta.Diagnostics;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Collects every completed <see cref="Activity"/> started on the shared <c>"Acta"</c>
/// <see cref="ActivitySource"/> (<see cref="ActaDiagnostics.ActivitySource"/>, task 8.6) via an
/// <see cref="ActivityListener"/> that samples every span (<see cref="ActivitySamplingResult.AllData"/>)
/// and records it once it stops.
/// <para>
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns <see langword="null"/>
/// unless at least one <see cref="ActivityListener"/> is both subscribed
/// (<see cref="ActivityListener.ShouldListenTo"/>) and sampling it — construct this collector BEFORE
/// running the code under test, or its spans are silently never created in the first place (not
/// merely uncollected).
/// </para>
/// <para>
/// <b>Cross-test isolation.</b> <see cref="ActaDiagnostics.ActivitySource"/> is one process-wide
/// singleton, and this <see cref="ActivityListener"/> — like every <see cref="ActivityListener"/> — has
/// no way to filter by "which test started this span"; xUnit runs different test classes concurrently
/// by default, so without correlation this collector would also pick up "Acta" spans a different,
/// simultaneously-running test emits (e.g. any other test appending to an <c>InMemoryEventStore</c>).
/// The constructor therefore starts a private root <see cref="Activity"/> (via the legacy
/// <see cref="Activity"/> API, deliberately NOT <see cref="ActaDiagnostics.ActivitySource"/> — it must
/// never itself appear in <see cref="Spans"/>) that becomes <see cref="Activity.Current"/> for the
/// calling async flow; every span the code under test starts afterwards inherits it as an implicit
/// parent (the same <see cref="Activity.TraceId"/>), which <see cref="FindSpan"/>/<see cref="FindSpans"/>
/// use to discard any collected span that does not belong to THIS instance's scope.
/// </para>
/// </summary>
public sealed class ActivitySpanCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly Activity _scope;

    /// <summary>Starts listening immediately — construct before the code under test starts any activity.</summary>
    public ActivitySpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActaDiagnostics.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);

        // See the "Cross-test isolation" remarks above — this legacy Activity is never observed by
        // _listener (it is not started from an ActivitySource), so it never lands in _stopped itself.
        _scope = new Activity(nameof(ActivitySpanCollector)).Start();
    }

    /// <summary>Every span collected so far within this instance's scope (stopped, in no particular cross-thread order).</summary>
    public IReadOnlyList<Activity> Spans => [.. _stopped.Where(InScope)];

    /// <summary>The first collected span named <paramref name="name"/> within this instance's scope (an <see cref="Activity.OperationName"/> match), or <see langword="null"/> if none was collected.</summary>
    public Activity? FindSpan(string name) => _stopped.FirstOrDefault(a => a.OperationName == name && InScope(a));

    /// <summary>
    /// Every collected span named <paramref name="name"/> within this instance's scope — needed for
    /// <see cref="ActaDiagnostics.ProjectionApplySpan"/> in particular, which is emitted once per
    /// (event, matched projection) apply call, never once per batch.
    /// </summary>
    public IReadOnlyList<Activity> FindSpans(string name) => [.. _stopped.Where(a => a.OperationName == name && InScope(a))];

    private bool InScope(Activity activity) => activity.TraceId == _scope.TraceId;

    /// <inheritdoc/>
    public void Dispose()
    {
        _scope.Stop();
        _listener.Dispose();
    }
}
