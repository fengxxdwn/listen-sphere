using System.IO;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Controller.Coordinators;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class SecondaryPlaybackRouteTests
{
    [Fact]
    public async Task DisposeAsync_WhenEndpointTeardownFails_DoesNotPropagateFailure()
    {
        var sink = new FailingPlaybackSink();
        var route = new SecondaryPlaybackRoute(sink);

        await route.DisposeAsync();

        Assert.Equal(1, sink.StopAttempts);
        Assert.Equal(1, sink.DisposeAttempts);
    }

    private sealed class FailingPlaybackSink : IAudioPlaybackSink
    {
        public AudioFormat InputFormat => AudioFormat.Default;
        public int StopAttempts { get; private set; }
        public int DisposeAttempts { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            AudioFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopAttempts++;
            return ValueTask.FromException(new IOException("stop failed"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return ValueTask.FromException(new IOException("dispose failed"));
        }
    }
}
