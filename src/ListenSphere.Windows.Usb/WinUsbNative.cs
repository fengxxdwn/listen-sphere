using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ListenSphere.Windows.Usb;

internal static class WinUsbNative
{
    internal static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint PipeTransferTimeout = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct UsbInterfaceDescriptor
    {
        internal byte Length;
        internal byte DescriptorType;
        internal byte InterfaceNumber;
        internal byte AlternateSetting;
        internal byte NumEndpoints;
        internal byte InterfaceClass;
        internal byte InterfaceSubClass;
        internal byte InterfaceProtocol;
        internal byte Interface;
    }

    internal enum UsbdPipeType
    {
        Control,
        Isochronous,
        Bulk,
        Interrupt
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PipeInformation
    {
        internal UsbdPipeType PipeType;
        internal byte PipeId;
        internal ushort MaximumPacketSize;
        internal byte Interval;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SetupPacket
    {
        internal byte RequestType;
        internal byte Request;
        internal ushort Value;
        internal ushort Index;
        internal ushort Length;
    }

    internal sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeWinUsbHandle() : base(true) { }

        protected override bool ReleaseHandle() => WinUsbFree(handle);
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern nint SetupDiGetClassDevs(
        ref Guid classGuid,
        nint enumerator,
        nint parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbInitialize(
        SafeFileHandle deviceHandle,
        out SafeWinUsbHandle interfaceHandle);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_Free", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsbFree(nint interfaceHandle);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbQueryInterfaceSettings(
        SafeWinUsbHandle interfaceHandle,
        byte alternateSettingNumber,
        out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbQueryPipe(
        SafeWinUsbHandle interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out PipeInformation pipeInformation);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbReadPipe(
        SafeWinUsbHandle interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbWritePipe(
        SafeWinUsbHandle interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbControlTransfer(
        SafeWinUsbHandle interfaceHandle,
        SetupPacket setupPacket,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        nint overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsbSetPipePolicy(
        SafeWinUsbHandle interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        ref uint value);

    internal static IOException Error(string operation) =>
        new($"{operation}失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
}
