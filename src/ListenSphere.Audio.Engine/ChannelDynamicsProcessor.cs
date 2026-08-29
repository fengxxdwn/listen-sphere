namespace ListenSphere.Audio.Engine;

public readonly record struct ChannelDynamicsSettings(
    float PreampDb = 0,
    bool NoiseGateEnabled = false,
    float NoiseGateThresholdDb = -48,
    bool CompressorEnabled = false,
    float CompressorThresholdDb = -18,
    float CompressorRatio = 4,
    bool LimiterEnabled = true,
    float LimiterCeilingDb = -1);

public readonly record struct ChannelDynamicsResult(
    float InputPeak,
    float OutputPeak,
    float GainReductionDb,
    bool GateClosed,
    bool Limited);

/// <summary>
/// Stateful, allocation-free dynamics chain for one interleaved Float32 channel.
/// Intended order: preamp, gate, compressor, then post-volume limiter.
/// </summary>
public sealed class ChannelDynamicsProcessor(int sampleRate = 48_000)
{
    private readonly int sampleRate = sampleRate > 0
        ? sampleRate
        : throw new ArgumentOutOfRangeException(nameof(sampleRate));
    private float gateGain = 1f;
    private float compressorGain = 1f;
    private float envelope;

    public ChannelDynamicsResult ProcessBeforeEqualizer(
        Span<float> samples,
        ChannelDynamicsSettings settings)
    {
        if (samples.IsEmpty)
        {
            return default;
        }

        float preamp = DbToGain(Math.Clamp(settings.PreampDb, -24f, 12f));
        float inputPeak = 0;
        double squareSum = 0;
        foreach (float raw in samples)
        {
            float sample = float.IsFinite(raw) ? raw * preamp : 0f;
            inputPeak = Math.Max(inputPeak, MathF.Abs(sample));
            squareSum += sample * sample;
        }

        float rms = MathF.Sqrt((float)(squareSum / samples.Length));
        float gateThreshold = DbToGain(Math.Clamp(settings.NoiseGateThresholdDb, -80f, -10f));
        bool gateClosed = settings.NoiseGateEnabled && rms < gateThreshold;
        float gateTarget = gateClosed ? 0f : 1f;
        float gateCoefficient = SmoothingCoefficient(
            gateTarget < gateGain ? 8f : 90f,
            samples.Length);
        gateGain += (gateTarget - gateGain) * gateCoefficient;

        float thresholdDb = Math.Clamp(settings.CompressorThresholdDb, -40f, 0f);
        float ratio = Math.Clamp(settings.CompressorRatio, 1f, 20f);
        float maximumReduction = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            float sample = float.IsFinite(samples[index]) ? samples[index] * preamp : 0f;
            float magnitude = MathF.Abs(sample);
            float envelopeCoefficient = magnitude > envelope
                ? SmoothingCoefficientPerSample(8f)
                : SmoothingCoefficientPerSample(120f);
            envelope += (magnitude - envelope) * envelopeCoefficient;

            float desiredCompressorGain = 1f;
            if (settings.CompressorEnabled && envelope > 0.000001f)
            {
                float envelopeDb = GainToDb(envelope);
                if (envelopeDb > thresholdDb)
                {
                    float outputDb = thresholdDb + ((envelopeDb - thresholdDb) / ratio);
                    float reductionDb = envelopeDb - outputDb;
                    maximumReduction = Math.Max(maximumReduction, reductionDb);
                    desiredCompressorGain = DbToGain(-reductionDb);
                }
            }

            float compressorCoefficient = desiredCompressorGain < compressorGain
                ? SmoothingCoefficientPerSample(10f)
                : SmoothingCoefficientPerSample(160f);
            compressorGain += (desiredCompressorGain - compressorGain) * compressorCoefficient;
            samples[index] = sample * gateGain * compressorGain;
        }

        float outputPeak = 0;
        foreach (float sample in samples)
        {
            outputPeak = Math.Max(outputPeak, MathF.Abs(sample));
        }
        return new ChannelDynamicsResult(
            inputPeak,
            outputPeak,
            maximumReduction,
            gateClosed && gateGain < 0.1f,
            false);
    }

    public ChannelDynamicsResult ApplyLimiter(
        Span<float> samples,
        ChannelDynamicsSettings settings,
        ChannelDynamicsResult previous)
    {
        float ceiling = DbToGain(Math.Clamp(settings.LimiterCeilingDb, -12f, -0.1f));
        float outputPeak = 0;
        bool limited = false;
        for (var index = 0; index < samples.Length; index++)
        {
            float sample = float.IsFinite(samples[index]) ? samples[index] : 0f;
            if (settings.LimiterEnabled && MathF.Abs(sample) > ceiling)
            {
                sample = MathF.CopySign(ceiling, sample);
                limited = true;
            }
            samples[index] = sample;
            outputPeak = Math.Max(outputPeak, MathF.Abs(sample));
        }

        return previous with { OutputPeak = outputPeak, Limited = limited };
    }

    public void Reset()
    {
        gateGain = 1f;
        compressorGain = 1f;
        envelope = 0f;
    }

    private float SmoothingCoefficient(float milliseconds, int sampleCount) =>
        1f - MathF.Exp(-sampleCount / (sampleRate * milliseconds / 1000f));

    private float SmoothingCoefficientPerSample(float milliseconds) =>
        1f - MathF.Exp(-1f / (sampleRate * milliseconds / 1000f));

    private static float DbToGain(float decibels) => MathF.Pow(10f, decibels / 20f);
    private static float GainToDb(float gain) => 20f * MathF.Log10(Math.Max(gain, 0.000001f));
}
