using Acta.Abstractions;

namespace Acta.Testing.Tests.TestSupport;

/// <summary>
/// A minimal event-sourced aggregate used to dogfood the <see cref="Spec"/> harness (AK-7).
/// Models a single on/off switch: <see cref="TurnOn"/> / <see cref="TurnOff"/> raise
/// <see cref="SwitchedOn"/> / <see cref="SwitchedOff"/> and each throws
/// <see cref="InvalidOperationException"/> when the switch is already in the requested state —
/// giving the harness's <c>ThenThrows</c> path a real command to catch.
/// </summary>
public sealed class LightSwitch : AggregateRoot
{
    private int _toggleCount;

    /// <summary>Whether the switch is currently on.</summary>
    public bool IsOn { get; private set; }

    /// <summary>Command: turns the switch on. Records a <see cref="SwitchedOn"/> event.</summary>
    /// <exception cref="InvalidOperationException">The switch is already on.</exception>
    public void TurnOn()
    {
        if (IsOn)
        {
            throw new InvalidOperationException("The switch is already on.");
        }

        Raise(new SwitchedOn(_toggleCount + 1));
    }

    /// <summary>Command: turns the switch off. Records a <see cref="SwitchedOff"/> event.</summary>
    /// <exception cref="InvalidOperationException">The switch is already off.</exception>
    public void TurnOff()
    {
        if (!IsOn)
        {
            throw new InvalidOperationException("The switch is already off.");
        }

        Raise(new SwitchedOff(_toggleCount + 1));
    }

    /// <summary>Total mutator (FR-11, AK-4): folds known events; any other type is a no-op.</summary>
    protected override void Apply(object @event)
    {
        switch (@event)
        {
            case SwitchedOn on:
                IsOn = true;
                _toggleCount = on.ToggleCount;
                break;
            case SwitchedOff off:
                IsOn = false;
                _toggleCount = off.ToggleCount;
                break;
            default:
                break;
        }
    }
}

/// <summary>Raised by <see cref="LightSwitch.TurnOn"/> — the switch transitioned to on.</summary>
/// <param name="ToggleCount">The 1-based count of on/off transitions, including this one.</param>
public sealed record SwitchedOn(int ToggleCount);

/// <summary>Raised by <see cref="LightSwitch.TurnOff"/> — the switch transitioned to off.</summary>
/// <param name="ToggleCount">The 1-based count of on/off transitions, including this one.</param>
public sealed record SwitchedOff(int ToggleCount);
