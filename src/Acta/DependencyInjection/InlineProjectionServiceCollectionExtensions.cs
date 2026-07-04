using Microsoft.Extensions.DependencyInjection.Extensions;

using Acta.Projections.Inline;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A narrow DI seam for the default in-memory <see cref="InlineProjectionRunner"/> (task 4.1,
/// MODULE-INTERFACES "Rejestracja DI"): registers a concrete <c>IProjection&lt;TEvent&gt;</c>
/// implementation so <see cref="InlineProjectionRunner"/> can discover it, plus the runner itself.
/// </summary>
public static class InlineProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TProjection"/> — a class implementing one or more closed
    /// <c>IProjection&lt;TEvent&gt;</c> interfaces — as a singleton, under the bare
    /// <see cref="object"/> service type so <see cref="InlineProjectionRunner"/>'s
    /// <c>IEnumerable&lt;object&gt;</c> constructor parameter picks it up (MS.DI resolves
    /// <c>IEnumerable&lt;object&gt;</c> only from descriptors registered literally as
    /// <see cref="object"/>), and registers <see cref="InlineProjectionRunner"/> itself as a
    /// singleton.
    /// <para>
    /// <b>Prerequisite: call <c>AddActa(...)</c> first.</b> <see cref="InlineProjectionRunner"/>
    /// depends on <c>EventSerializer</c> and <c>EventTypeRegistry</c>, both registered by
    /// <c>AddActa</c> — resolving <see cref="InlineProjectionRunner"/> without a prior
    /// <c>AddActa(...)</c> call on the same <see cref="IServiceCollection"/> throws at resolution
    /// time (the container cannot satisfy those constructor parameters).
    /// </para>
    /// <para>
    /// Registration is idempotent (<see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// / <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type)"/>):
    /// calling this method more than once for the same <typeparamref name="TProjection"/> does not
    /// duplicate its service descriptor, nor the runner's.
    /// </para>
    /// <para>
    /// This method does NOT wire <see cref="InlineProjectionRunner"/> into the append flow —
    /// invoking <see cref="InlineProjectionRunner.RunAsync"/> after each append remains the host's
    /// own responsibility for this task (4.1); an automatic seam is a deliberate follow-up.
    /// </para>
    /// </summary>
    /// <typeparam name="TProjection">
    /// The concrete projection type to register; must implement at least one closed
    /// <c>IProjection&lt;TEvent&gt;</c>.
    /// </typeparam>
    /// <param name="services">The service collection to register the projection into.</param>
    /// <returns><paramref name="services"/>, to allow fluent chaining.</returns>
    public static IServiceCollection AddInlineProjection<TProjection>(this IServiceCollection services)
        where TProjection : class
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<object, TProjection>());
        services.TryAddSingleton<InlineProjectionRunner>();
        return services;
    }
}
