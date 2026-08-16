using System;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class ClipboardSuppressionGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    [Fact]
    public void ShouldSuppress_WithNothingArmed_CapturesNormally()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_AfterArm_IgnoresExactlyOneNotification()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();

        Assert.True(gate.ShouldSuppress());
        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_WhenTheArmedWriteNeverRaisedANotification_DoesNotSwallowALaterRealCopy()
    {
        // The write failed, or set identical content, so Windows raised nothing. Before the
        // expiry window existed the suppression stayed armed forever and the user's next
        // genuine copy vanished with no error anywhere.
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();
        clock.Advance(Window + TimeSpan.FromMilliseconds(1));

        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_AtTheEdgeOfTheWindow_StillSuppresses()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();
        clock.Advance(Window);

        Assert.True(gate.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_WithSeveralArmsCoalescedIntoOneNotification_ClearsThemAll()
    {
        // The monitor coalesces rapid changes, so three writes can produce one notification.
        // Whatever is left over must not survive to eat a real copy.
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();
        gate.Arm();
        gate.Arm();

        Assert.True(gate.ShouldSuppress());
        Assert.False(gate.ShouldSuppress());
    }

    [Fact]
    public void Arm_RefreshesTheDeadlineForAnAlreadyPendingSuppression()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();
        clock.Advance(Window - TimeSpan.FromMilliseconds(1));
        gate.Arm();
        clock.Advance(Window - TimeSpan.FromMilliseconds(1));

        Assert.True(gate.ShouldSuppress());
    }

    private sealed class FakeClock
    {
        private long _milliseconds;

        public long Read() => _milliseconds;

        public void Advance(TimeSpan amount) => _milliseconds += (long)amount.TotalMilliseconds;
    }
}
