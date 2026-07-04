using Acta.Testing;
using Acta.Testing.Tests.TestSupport;

namespace Acta.Testing.Tests;

/// <summary>
/// Unit tests for the <see cref="Spec"/> Given-When-Then harness mechanics (task 4.2, dogfooding
/// AK-7): the <see cref="SpecThen{T}.Then"/> / <see cref="SpecThen{T}.ThenThrows{TException}"/>
/// pass and fail paths, history rehydration order, the creating-command shortcut
/// (<see cref="SpecFor{T}.When"/> without <see cref="SpecFor{T}.Given"/>), and the
/// <c>ArgumentNullException</c> guard delegated through to <c>AggregateRoot.LoadFromHistory</c>.
/// Every failure-path test asserts the harness DOES throw <see cref="SpecAssertionException"/> —
/// the anti-pattern check that the harness genuinely detects mismatches, not just happy paths.
/// </summary>
public sealed class SpecTests
{
    [Fact]
    public async Task Then_MatchingEvents_DoesNotThrow()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOff())
            .Then(new SwitchedOff(2));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Then_EventCountMismatch_ThrowsSpecAssertionException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .When(s => s.TurnOn())
            .Then(new SwitchedOn(1), new SwitchedOn(2));

        await act.Should().ThrowAsync<SpecAssertionException>();
    }

    [Fact]
    public async Task Then_SameEventTypeDifferentFieldValue_ThrowsSpecAssertionException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOff())
            .Then(new SwitchedOff(99));

        await act.Should().ThrowAsync<SpecAssertionException>();
    }

    [Fact]
    public async Task Then_CommandThrewUnexpectedException_ThrowsSpecAssertionExceptionWithInnerException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOn())
            .Then(new SwitchedOn(2));

        (await act.Should().ThrowAsync<SpecAssertionException>())
            .WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public async Task Given_History_RehydratesStateBeforeWhenRuns()
    {
        var wasOnBeforeCommand = false;

        await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s =>
            {
                wasOnBeforeCommand = s.IsOn;
                s.TurnOff();
            })
            .Then(new SwitchedOff(2));

        wasOnBeforeCommand.Should().BeTrue();
    }

    [Fact]
    public async Task When_NoGivenHistory_CreatingCommandProducesSingleEvent()
    {
        await Spec.For<LightSwitch>()
            .When(s => s.TurnOn())
            .Then(new SwitchedOn(1));
    }

    [Fact]
    public async Task ThenThrows_ExactExceptionType_DoesNotThrow()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOn())
            .ThenThrows<InvalidOperationException>();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThenThrows_ActionThrowsSubtypeOfExpectedException_DoesNotThrow()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOn())
            .ThenThrows<Exception>();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThenThrows_CommandDoesNotThrow_ThrowsSpecAssertionException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .When(s => s.TurnOn())
            .ThenThrows<InvalidOperationException>();

        await act.Should().ThrowAsync<SpecAssertionException>();
    }

    [Fact]
    public async Task ThenThrows_WrongExceptionType_ThrowsSpecAssertionExceptionWithActualAsInnerException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(new SwitchedOn(1))
            .When(s => s.TurnOn())
            .ThenThrows<ArgumentException>();

        (await act.Should().ThrowAsync<SpecAssertionException>())
            .WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public async Task Given_NullHistory_ThrowsArgumentNullException()
    {
        var act = async () => await Spec.For<LightSwitch>()
            .Given(null!)
            .When(s => s.TurnOn())
            .Then(new SwitchedOn(1));

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
