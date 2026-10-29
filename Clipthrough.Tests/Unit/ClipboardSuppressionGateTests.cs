using System;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class ClipboardSuppressionGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    // ─── Re-published self-writes (F-52) ──────────────────────────────────────
    //
    // Over Remote Desktop, rdpclip re-publishes every clipboard write, which
    // raises a second change notification 100-300 ms later - past the
    // monitor's 60 ms coalescing. One Arm swallowed one notification, so every
    // paste Clipthrough made was captured again: copy counts rose by two per
    // paste and step-paste reordered the list under the user.

    [Fact]
    public void ARepublishOfTheArmedContent_IsAlsoRecognised()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm("pasted text");

        Assert.True(gate.ShouldSuppress());
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(gate.ShouldSuppress());
        Assert.True(gate.IsRecentSelfWrite("pasted text"));
    }

    [Fact]
    public void ARepublishWithDifferentLineEndings_IsStillRecognised()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm("line one\nline two");
        gate.ShouldSuppress();

        Assert.True(gate.IsRecentSelfWrite("line one\r\nline two"));
    }

    [Fact]
    public void DifferentContentInsideTheWindow_IsStillCaptured()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm("pasted text");
        gate.ShouldSuppress();

        Assert.False(gate.IsRecentSelfWrite("something the user copied"));
    }

    [Fact]
    public void TheSameContentAfterTheWindow_IsCaptured()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm("pasted text");
        gate.ShouldSuppress();
        clock.Advance(Window + TimeSpan.FromMilliseconds(1));

        Assert.False(gate.IsRecentSelfWrite("pasted text"));
    }

    [Fact]
    public void AWriteWithNoText_RecognisesNoRepublish()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm();
        gate.ShouldSuppress();

        Assert.False(gate.IsRecentSelfWrite(null));
        Assert.False(gate.IsRecentSelfWrite(string.Empty));
    }

    [Fact]
    public void AWithdrawnWrite_IsNotRememberedAsWritten()
    {
        var clock = new FakeClock();
        var gate = new ClipboardSuppressionGate(Window, clock.Read);

        gate.Arm("never landed");
        gate.Disarm();

        Assert.False(gate.IsRecentSelfWrite("never landed"));
    }

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
