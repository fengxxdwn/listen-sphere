using System.Diagnostics;
using System.Globalization;
using ListenSphere.Audio.Engine;
using ListenSphere.Network;
using Xunit;

namespace ListenSphere.Core.Tests;

[Collection("Audio performance")]
public sealed class AudioPipelineSoakTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EncryptedLoopbackPipelineRemainsBounded()
    {
        string? requested = Environment.GetEnvironmentVariable("LISTENSPHERE_SOAK_MINUTES");
        double minutes = requested is null ? 0.05 : double.Parse(requested, CultureInfo.InvariantCulture);
        Assert.InRange(minutes, 0.05, 120);
        var duration = TimeSpan.FromMinutes(minutes);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(duration + TimeSpan.FromSeconds(30));
        await using var receiver = new UdpAudioReceiver();
        await receiver.StartAsync(cancellationToken: cancellation.Token);
        var parameters = AudioAllocationTests.Session(receiver.Port);
        receiver.RegisterSession(parameters with { Key = parameters.Key.ToArray() });
        await using var sender = new UdpAudioSender(parameters);
        var mixer = new RemotePcmMixer(3840, startupFrames: 1);
        mixer.RegisterStream(parameters.SessionId);
        byte[] pcm = new byte[3840];
        // Non-zero input makes stale/shared-buffer regressions observable without touching a sound device.
        Array.Fill(pcm, (byte)0x3d);
        byte[] mixed = new byte[3840];
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in receiver.ReadAllAsync(readerCancellation.Token))
                {
                    if (!frame.IsConcealment) Assert.True(pcm.AsSpan().SequenceEqual(frame.Pcm), "PCM changed across asynchronous delivery.");
                    mixer.Enqueue(frame.SessionId, frame.Pcm);
                    Assert.True(mixer.TryMixNext(mixed));
                }
            }
            catch (OperationCanceledException) when (readerCancellation.IsCancellationRequested) { }
        }, cancellation.Token);

        string? csv = Environment.GetEnvironmentVariable("LISTENSPHERE_SOAK_CSV");
        using var writer = csv is null ? null : new StreamWriter(csv, append: false);
        writer?.WriteLine("Seconds,AllocatedBytes,HeapBytes,PrivateBytes,WorkingSetBytes,Gen0,Gen1,Gen2,Threads,Handles,Frames,Lost,Concealed,OutputOverflows,JitterFrames,OutputDepth,MixerUnderflows,MixerOverflows");
        using var process = Process.GetCurrentProcess();
        var timer = Stopwatch.StartNew();
        var samples = new List<Sample>();
        double nextSample = 0;
        uint sequence = 0;
        using var pacing = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (timer.Elapsed < duration && await pacing.WaitForNextTickAsync(cancellation.Token))
            {
                if (reader.IsFaulted) await reader;
                // Windows timer resolution may exceed 10 ms; keep the intended 100 frames/s load.
                uint due = (uint)(Math.Min(timer.Elapsed.TotalSeconds, duration.TotalSeconds) * 100);
                Assert.True(due - sequence <= 200, "Test host fell over two seconds behind real time.");
                while (sequence < due)
                    await sender.SendFrameAsync(pcm, sequence++ * 480UL, cancellation.Token);
                if (timer.Elapsed.TotalSeconds < nextSample) continue;
                nextSample = timer.Elapsed.TotalSeconds + (minutes >= 1 ? 5 : 0.2);
                process.Refresh();
                var network = receiver.Statistics;
                var audio = mixer.Statistics;
                var sample = new Sample(timer.Elapsed.TotalSeconds, GC.GetTotalMemory(false),
                    process.PrivateMemorySize64, process.Threads.Count, process.HandleCount);
                samples.Add(sample);
                writer?.WriteLine(FormattableString.Invariant(
                    $"{sample.Seconds:F2},{GC.GetTotalAllocatedBytes(false)},{sample.Heap},{sample.Private},{process.WorkingSet64},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{sample.Threads},{sample.Handles},{network.FramesCompleted},{network.EstimatedLostDatagrams},{network.ConcealmentFrames},{network.OutputOverflows},{network.JitterBufferedFrames},{network.OutputQueueDepth},{audio.StreamUnderflows},{audio.StreamOverflows}"));
                writer?.Flush();
            }
        }
        finally
        {
            await readerCancellation.CancelAsync();
            await reader;
        }

        var steady = samples.Where(s => s.Seconds >= (minutes >= 1 ? 60 : 0.5)).ToArray();
        Assert.NotEmpty(steady);
        Assert.True(sequence >= duration.TotalSeconds * 98, "The requested real-time load was not achieved.");
        var final = receiver.Statistics;
        double slope = Slope(steady) * 60 / (1024 * 1024);
        output.WriteLine($"Duration={timer.Elapsed}; sent={sequence}; completed={final.FramesCompleted}; private slope={slope:F3} MiB/min");
        Assert.True(final.FramesCompleted >= sequence * 0.98, "Too many frames lost on loopback.");
        Assert.Equal(0, final.OutputOverflows);
        Assert.Equal(0, final.AuthenticationFailures);
        Assert.InRange(final.OutputQueueDepth, 0, 128);
        Assert.InRange(final.JitterBufferedFrames, 0, 12);
        Assert.Equal(0, mixer.Statistics.StreamOverflows);
        Assert.Equal(0, mixer.Statistics.StreamUnderflows);
        if (minutes >= 2)
        {
            Assert.True(slope < 0.5, $"Private memory grew linearly: {slope:F3} MiB/min");
            Assert.True(steady[^1].Private - steady[0].Private < 32L * 1024 * 1024);
            Assert.True(steady[^1].Threads - steady[0].Threads <= 16);
            Assert.True(steady[^1].Handles - steady[0].Handles <= 32);
        }
    }

    private static double Slope(Sample[] samples)
    {
        double time = samples.Average(s => s.Seconds);
        double memory = samples.Average(s => (double)s.Private);
        double denominator = samples.Sum(s => Math.Pow(s.Seconds - time, 2));
        return denominator == 0 ? 0 : samples.Sum(s => (s.Seconds - time) * (s.Private - memory)) / denominator;
    }

    private sealed record Sample(double Seconds, long Heap, long Private, int Threads, int Handles);
}
