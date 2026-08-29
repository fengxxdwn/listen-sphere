using System.Diagnostics;
using ListenSphere.Windows.Bluetooth;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class BluetoothQualityTests
{
    [Fact]
    public void TimestampTracker_DetectsMissingFrameAndArrivalJitter()
    {
        var tracker = new BluetoothTimestampQualityTracker(480);
        long tenMilliseconds = Stopwatch.Frequency / 100;

        tracker.Observe(0, 0);
        tracker.Observe(480, tenMilliseconds);
        tracker.Observe(1_440, tenMilliseconds * 2);

        Assert.Equal(1, tracker.TimestampGaps);
        Assert.True(tracker.EstimatedJitterMilliseconds > 0);
        Assert.InRange(tracker.EstimatedJitterMilliseconds, 0, 10);
    }
}
