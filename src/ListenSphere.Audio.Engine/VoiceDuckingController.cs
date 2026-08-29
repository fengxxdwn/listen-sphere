using System.Diagnostics;

namespace ListenSphere.Audio.Engine;

/// <summary>Applies smooth, held attenuation to non-voice channels while voice is active.</summary>
public sealed class VoiceDuckingController(
    float voiceThresholdDb = -42,
    int holdMilliseconds = 350,
    int attackMilliseconds = 60,
    int releaseMilliseconds = 450)
{
    private readonly object gate = new();
    private long voiceActiveUntil;
    private long lastUpdate;
    private float currentGain = 1f;

    public bool IsVoiceActive => Stopwatch.GetTimestamp() < Volatile.Read(ref voiceActiveUntil);

    public void ObserveVoice(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }
        double sum = 0;
        foreach (float sample in samples)
        {
            float finite = float.IsFinite(sample) ? sample : 0f;
            sum += finite * finite;
        }
        float rms = MathF.Sqrt((float)(sum / samples.Length));
        float threshold = MathF.Pow(10f, Math.Clamp(voiceThresholdDb, -80f, -10f) / 20f);
        if (rms >= threshold)
        {
            Volatile.Write(
                ref voiceActiveUntil,
                Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * holdMilliseconds / 1000d));
        }
    }

    public float GetTargetGain(float reductionDb)
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastUpdate == 0)
            {
                lastUpdate = now;
            }
            double elapsedMs = Math.Max(0, (now - lastUpdate) * 1000d / Stopwatch.Frequency);
            lastUpdate = now;
            bool active = now < Volatile.Read(ref voiceActiveUntil);
            float target = active
                ? MathF.Pow(10f, -Math.Clamp(reductionDb, 0f, 30f) / 20f)
                : 1f;
            int duration = target < currentGain ? attackMilliseconds : releaseMilliseconds;
            float coefficient = duration <= 0
                ? 1f
                : 1f - MathF.Exp(-(float)elapsedMs / duration);
            currentGain += (target - currentGain) * coefficient;
            return currentGain;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            voiceActiveUntil = 0;
            lastUpdate = 0;
            currentGain = 1f;
        }
    }
}
