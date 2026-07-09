using Xunit;

using Acta.Diagnostics;

namespace Acta.Tests.Diagnostics;

/// <summary>
/// Direct unit tests for <see cref="ActaDiagnostics"/> (task 8.6): the shared
/// <see cref="ActaDiagnostics.ActivitySource"/>'s <c>Version</c> must reflect the actual assembly
/// version, not unconditionally fall back to the null-safety placeholder.
/// </summary>
public sealed class ActaDiagnosticsTests
{
    [Fact]
    public void ActivitySource_Version_ReflectsActualAssemblyVersion_NotTheNullFallbackConstant()
    {
        // Independently recomputed with the SAME (unmutated, since this is a different compiland)
        // formula the source uses: a mutation that discards the actual assembly version and always
        // forces the "0.0.0" placeholder is only caught if the real version differs from it — which
        // it does for any normally-built assembly (the SDK always assigns a non-null AssemblyVersion).
        var expectedVersion = typeof(ActaDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        ActaDiagnostics.ActivitySource.Version.Should().Be(expectedVersion);
        ActaDiagnostics.ActivitySource.Version.Should().NotBe("0.0.0");
    }
}
