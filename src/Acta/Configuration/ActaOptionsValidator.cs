using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Configuration;

/// <summary>
/// Fail-fast validator for <see cref="ActaOptions"/>, wired into <c>AddActa()</c> via
/// <c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IValidateOptions&lt;ActaOptions&gt;,
/// ActaOptionsValidator&gt;())</c> and <c>optionsBuilder.ValidateOnStart()</c> — the "fail-fast
/// przy starcie hosta" gate (05-implementation §6). No dependency on
/// <c>Microsoft.Extensions.Hosting</c> is required: the generic host's
/// <see cref="IStartupValidator"/> (registered by <c>ValidateOnStart</c>) calls every registered
/// <see cref="IValidateOptions{TOptions}"/> at startup and throws
/// <see cref="OptionsValidationException"/> on the first failure.
/// <para>
/// Beyond the blocking null checks, this validator also emits two non-blocking startup
/// <see cref="LogLevel.Warning"/> log entries: the D14/ADR-014 "single-process only" notice for
/// the in-memory backend, and an empty-registry notice when no event types have been registered.
/// Because both the built-in <see cref="IOptionsMonitor{TOptions}"/> cache and
/// <see cref="ValidateOnStart"/>'s own resolution path are independent caches, this validator may
/// run — and therefore may log — more than once per process startup; callers must not assert an
/// exact log count, only that the expected message appeared at least once.
/// </para>
/// </summary>
/// <param name="logger">The logger the startup warnings are written to.</param>
internal sealed class ActaOptionsValidator(ILogger<ActaOptionsValidator> logger) : IValidateOptions<ActaOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ActaOptions options)
    {
        var failures = new List<string>();

        if (options.SerializerOptions is null)
        {
            failures.Add($"{nameof(ActaOptions.SerializerOptions)} must not be null.");
        }

        if (options.Events is null)
        {
            failures.Add($"{nameof(ActaOptions.Events)} must not be null.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        // Null-forgiving below: nullable flow analysis cannot correlate "failures is empty" with
        // "the two properties checked above are non-null" across the accumulate-then-check-count
        // shape used here (each null-check branch above doesn't itself return, so the compiler's
        // flow-sensitive null state for Events/SerializerOptions reverts to "maybe null" once the
        // two branches merge) — by construction, reaching this point means both checks passed.
        if (options.Events!.Count == 0)
        {
            logger.LogWarning(
                "AddActa: no event types are registered in ActaOptions.Events — the in-memory store cannot round-trip any event until types are registered via configure(o => o.Events.Register<T>(...)).");
        }

        // D14 / ADR-014: the in-memory backend is SINGLE-PROCESS ONLY.
        logger.LogWarning(
            "AddActa registered the in-memory event store — SINGLE-PROCESS ONLY (ADR-014, D14). For any topology with more than one pod use AddActaPostgres. In-memory guarantees hold only within one process.");

        return ValidateOptionsResult.Success;
    }
}
