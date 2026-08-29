using System.Buffers.Binary;

namespace ListenSphere.Audio.Engine;

public static class ImaAdpcmCodec
{
    public const int SamplesPerFrame = 480;
    public const int EncodedBytesPerFrame = 244;
    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] StepTable = [7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767];

    public static void Encode(ReadOnlySpan<short> samples, Span<byte> destination)
    {
        if (samples.Length != SamplesPerFrame || destination.Length < EncodedBytesPerFrame) throw new ArgumentException("IMA ADPCM frame size is invalid.");
        int predictor = samples[0]; int index = 0;
        BinaryPrimitives.WriteInt16LittleEndian(destination, (short)predictor);
        destination[2] = 0; destination[3] = 0; destination[4..EncodedBytesPerFrame].Clear();
        for (int sample = 1; sample < SamplesPerFrame; sample++)
        {
            int nibble = EncodeSample(samples[sample], ref predictor, ref index);
            int nibbleIndex = sample - 1; int byteIndex = 4 + (nibbleIndex / 2);
            if ((nibbleIndex & 1) == 0) destination[byteIndex] = (byte)nibble;
            else destination[byteIndex] |= (byte)(nibble << 4);
        }
    }

    public static void Decode(ReadOnlySpan<byte> encoded, Span<short> destination)
    {
        if (encoded.Length != EncodedBytesPerFrame || destination.Length < SamplesPerFrame) throw new ArgumentException("IMA ADPCM block length is invalid.");
        int predictor = BinaryPrimitives.ReadInt16LittleEndian(encoded); int index = encoded[2];
        if (index > 88) throw new InvalidDataException("IMA ADPCM step index is invalid.");
        destination[0] = (short)predictor;
        for (int sample = 1; sample < SamplesPerFrame; sample++)
        {
            int nibbleIndex = sample - 1; byte packed = encoded[4 + (nibbleIndex / 2)];
            int nibble = (nibbleIndex & 1) == 0 ? packed & 15 : packed >> 4;
            predictor = DecodeNibble(nibble, predictor, ref index); destination[sample] = (short)predictor;
        }
    }

    private static int EncodeSample(int sample, ref int predictor, ref int index)
    {
        int step = StepTable[index]; int difference = sample - predictor; int nibble = 0;
        if (difference < 0) { nibble = 8; difference = -difference; }
        int delta = step >> 3;
        if (difference >= step) { nibble |= 4; difference -= step; delta += step; }
        if (difference >= step >> 1) { nibble |= 2; difference -= step >> 1; delta += step >> 1; }
        if (difference >= step >> 2) { nibble |= 1; delta += step >> 2; }
        predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -delta : delta), short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88); return nibble;
    }

    private static int DecodeNibble(int nibble, int predictor, ref int index)
    {
        int step = StepTable[index]; int delta = step >> 3;
        if ((nibble & 4) != 0) delta += step; if ((nibble & 2) != 0) delta += step >> 1; if ((nibble & 1) != 0) delta += step >> 2;
        predictor = Math.Clamp(predictor + ((nibble & 8) != 0 ? -delta : delta), short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88); return predictor;
    }
}
