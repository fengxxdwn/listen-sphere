using ListenSphere.Windows.Bluetooth;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class AacDecoderTechnicalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WindowsMediaFoundationAcceptsAacAdtsFormat(int channelCount)
    {
        using var decoder = new AacAdtsDecoder(channelCount);
    }
}
