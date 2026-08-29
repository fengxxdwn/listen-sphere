using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ListenSphere.Windows.Bluetooth;

/// <summary>Stateful Windows Media Foundation AAC-LC/ADTS decoder.</summary>
internal sealed class AacAdtsDecoder : IDisposable
{
    private static readonly Guid AacDecoderClsid = new("32d186a7-218f-4c75-8876-dd77273a8999");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private static readonly object StartupGate = new();
    private static bool mediaFoundationStarted;
    private readonly IMFTransform transform;
    private readonly int channelCount;
    private long sampleTime;
    private bool disposed;

    public AacAdtsDecoder(int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelCount, 2);
        this.channelCount = channelCount;
        EnsureMediaFoundationStarted();

        Type decoderType = Type.GetTypeFromCLSID(AacDecoderClsid, throwOnError: true)!;
        transform = (IMFTransform)Activator.CreateInstance(decoderType)!;

        var input = new MediaType();
        input.MajorType = MediaTypes.MFMediaType_Audio;
        input.SubType = AudioSubtypes.MFAudioFormat_ADTS;
        input.SampleRate = 48_000;
        input.ChannelCount = channelCount;
        IMFAttributes inputAttributes = input.MediaFoundationObject;
        inputAttributes.SetUINT32(
            MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
            channelCount == 2 ? 20_000 : 12_000);
        inputAttributes.SetUINT32(MediaFoundationAttributes.MF_MT_COMPRESSED, 1);
        inputAttributes.SetUINT32(MediaFoundationAttributes.MF_MT_AAC_PAYLOAD_TYPE, 1);
        transform.SetInputType(0, input.MediaFoundationObject, _MFT_SET_TYPE_FLAGS.None);

        var output = new MediaType(new WaveFormat(48_000, 16, channelCount));
        transform.SetOutputType(0, output.MediaFoundationObject, _MFT_SET_TYPE_FLAGS.None);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    public byte[] Decode(ReadOnlySpan<byte> adtsFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (adtsFrame.Length is < 8 or > 8192 || adtsFrame[0] != 0xff || (adtsFrame[1] & 0xf6) != 0xf0)
        {
            throw new InvalidDataException("AAC ADTS frame is invalid.");
        }

        IMFSample inputSample = MediaFoundationApi.CreateSample();
        IMFMediaBuffer inputBuffer = MediaFoundationApi.CreateMemoryBuffer(adtsFrame.Length);
        IntPtr inputPointer = IntPtr.Zero;
        try
        {
            inputBuffer.Lock(out inputPointer, out _, out _);
            Marshal.Copy(adtsFrame.ToArray(), 0, inputPointer, adtsFrame.Length);
            inputBuffer.Unlock();
            inputPointer = IntPtr.Zero;
            inputBuffer.SetCurrentLength(adtsFrame.Length);
            inputSample.AddBuffer(inputBuffer);
            inputSample.SetSampleTime(sampleTime);
            inputSample.SetSampleDuration(10_000_000L * 1024 / 48_000);
            sampleTime += 10_000_000L * 1024 / 48_000;
            transform.ProcessInput(0, inputSample, 0);
        }
        finally
        {
            if (inputPointer != IntPtr.Zero) inputBuffer.Unlock();
            Marshal.ReleaseComObject(inputBuffer);
            Marshal.ReleaseComObject(inputSample);
        }

        var decoded = new List<byte>(1024 * channelCount * sizeof(short));
        while (true)
        {
            transform.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO streamInfo);
            int capacity = Math.Max(streamInfo.cbSize, 4096 * channelCount);
            IMFSample outputSample = MediaFoundationApi.CreateSample();
            IMFMediaBuffer outputBuffer = MediaFoundationApi.CreateMemoryBuffer(capacity);
            IMFMediaBuffer? contiguous = null;
            try
            {
                outputSample.AddBuffer(outputBuffer);
                var outputData = new[]
                {
                    new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = outputSample }
                };
                int result = transform.ProcessOutput(
                    _MFT_PROCESS_OUTPUT_FLAGS.None, 1, outputData, out _);
                if (result == NeedMoreInput) break;
                if (result < 0) Marshal.ThrowExceptionForHR(result);

                outputSample.ConvertToContiguousBuffer(out contiguous);
                IntPtr outputPointer = IntPtr.Zero;
                try
                {
                    contiguous.Lock(out outputPointer, out _, out int length);
                    if (length > 0)
                    {
                        byte[] chunk = new byte[length];
                        Marshal.Copy(outputPointer, chunk, 0, length);
                        decoded.AddRange(chunk);
                    }
                }
                finally
                {
                    if (outputPointer != IntPtr.Zero) contiguous.Unlock();
                }
            }
            finally
            {
                if (contiguous is not null && !ReferenceEquals(contiguous, outputBuffer))
                {
                    Marshal.ReleaseComObject(contiguous);
                }
                Marshal.ReleaseComObject(outputBuffer);
                Marshal.ReleaseComObject(outputSample);
            }
        }
        return decoded.ToArray();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        Marshal.FinalReleaseComObject(transform);
    }

    private static void EnsureMediaFoundationStarted()
    {
        lock (StartupGate)
        {
            if (mediaFoundationStarted) return;
            MediaFoundationApi.Startup();
            mediaFoundationStarted = true;
        }
    }
}
