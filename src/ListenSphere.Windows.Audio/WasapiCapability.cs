using NAudio.Wave;

namespace ListenSphere.Windows.Audio;

/// <summary>P0 marker proving that the Windows adapter resolves the stable NAudio API.</summary>
public static class WasapiCapability
{
    public static Type PlaybackAdapterType => typeof(WasapiOut);
    public static Type LoopbackCaptureAdapterType => typeof(WasapiLoopbackCapture);
}

