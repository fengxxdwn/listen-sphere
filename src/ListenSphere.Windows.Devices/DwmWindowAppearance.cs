using System.Runtime.InteropServices;

namespace ListenSphere.Windows.Devices;

/// <summary>Applies supported native DWM appearance attributes to a top-level window.</summary>
public static class DwmWindowAppearance
{
    private const uint WindowCornerPreferenceAttribute = 33;

    public static bool TryEnableRoundedCorners(nint windowHandle)
    {
        if (windowHandle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return false;
        }

        try
        {
            int preference = (int)DwmWindowCornerPreference.Round;
            int result = DwmSetWindowAttribute(
                windowHandle,
                WindowCornerPreferenceAttribute,
                ref preference,
                sizeof(int));
            return result >= 0;
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        uint attribute,
        ref int value,
        int valueSize);

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }
}
