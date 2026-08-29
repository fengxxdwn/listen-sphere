using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ListenSphere.Windows.Usb;

internal sealed class WinUsbBulkConnection : IAsyncDisposable
{
    private readonly SafeFileHandle deviceHandle;
    private readonly WinUsbNative.SafeWinUsbHandle interfaceHandle;
    private readonly byte bulkIn;
    private readonly byte bulkOut;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int disposed;

    private WinUsbBulkConnection(
        string devicePath,
        SafeFileHandle deviceHandle,
        WinUsbNative.SafeWinUsbHandle interfaceHandle,
        byte bulkIn,
        byte bulkOut)
    {
        DevicePath = devicePath;
        this.deviceHandle = deviceHandle;
        this.interfaceHandle = interfaceHandle;
        this.bulkIn = bulkIn;
        this.bulkOut = bulkOut;
        uint timeout = 1_000;
        WinUsbNative.WinUsbSetPipePolicy(interfaceHandle, bulkIn,
            WinUsbNative.PipeTransferTimeout, sizeof(uint), ref timeout);
        WinUsbNative.WinUsbSetPipePolicy(interfaceHandle, bulkOut,
            WinUsbNative.PipeTransferTimeout, sizeof(uint), ref timeout);
    }

    internal string DevicePath { get; }

    internal static IReadOnlyList<string> EnumerateDevicePaths()
    {
        Guid guid = WinUsbNative.UsbDeviceInterface;
        nint infoSet = WinUsbNative.SetupDiGetClassDevs(
            ref guid, 0, 0, WinUsbNative.DigcfPresent | WinUsbNative.DigcfDeviceInterface);
        if (infoSet == -1) return [];
        try
        {
            var paths = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new WinUsbNative.DeviceInterfaceData
                {
                    Size = checked((uint)Marshal.SizeOf<WinUsbNative.DeviceInterfaceData>())
                };
                if (!WinUsbNative.SetupDiEnumDeviceInterfaces(infoSet, 0, ref guid, index, ref data))
                {
                    break;
                }

                _ = WinUsbNative.SetupDiGetDeviceInterfaceDetail(
                    infoSet, ref data, 0, 0, out uint required, 0);
                nint detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (WinUsbNative.SetupDiGetDeviceInterfaceDetail(
                        infoSet, ref data, detail, required, out _, 0))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            return paths;
        }
        finally
        {
            WinUsbNative.SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    internal static bool IsAccessoryPath(string path)
    {
        string normalized = path.ToLowerInvariant();
        return normalized.Contains("vid_18d1&pid_2d00", StringComparison.Ordinal) ||
            normalized.Contains("vid_18d1&pid_2d01", StringComparison.Ordinal) ||
            normalized.Contains("vid_18d1&pid_2d04", StringComparison.Ordinal) ||
            normalized.Contains("vid_18d1&pid_2d05", StringComparison.Ordinal);
    }

    private static bool IsKnownAndroidPath(string path)
    {
        string normalized = path.ToLowerInvariant();
        string[] vendorIds =
        [
            "vid_18d1", // Google / AOA
            "vid_2717", // Xiaomi
            "vid_04e8", // Samsung
            "vid_12d1", // Huawei
            "vid_2a70", // OnePlus
            "vid_22d9", // OPPO
            "vid_2d95", // vivo
            "vid_22b8", // Motorola
            "vid_0fce", // Sony
            "vid_1004", // LG
            "vid_0bb4"  // HTC
        ];
        return vendorIds.Any(normalized.Contains);
    }

    internal static WinUsbBulkConnection? TryOpenAccessory(string path)
    {
        if (!IsAccessoryPath(path)) return null;
        if (!TryInitialize(path, out SafeFileHandle? device, out WinUsbNative.SafeWinUsbHandle? usb))
        {
            return null;
        }
        try
        {
            if (!WinUsbNative.WinUsbQueryInterfaceSettings(usb!, 0, out var descriptor)) return null;
            byte input = 0;
            byte output = 0;
            for (byte index = 0; index < descriptor.NumEndpoints; index++)
            {
                if (!WinUsbNative.WinUsbQueryPipe(usb!, 0, index, out var pipe) ||
                    pipe.PipeType != WinUsbNative.UsbdPipeType.Bulk) continue;
                if ((pipe.PipeId & 0x80) != 0) input = pipe.PipeId;
                else output = pipe.PipeId;
            }
            if (input == 0 || output == 0) return null;
            var connection = new WinUsbBulkConnection(path, device!, usb!, input, output);
            device = null;
            usb = null;
            return connection;
        }
        finally
        {
            usb?.Dispose();
            device?.Dispose();
        }
    }

    internal static bool TryActivateAccessory(string path, string serial)
    {
        if (IsAccessoryPath(path) || !IsKnownAndroidPath(path) ||
            !TryInitialize(path, out SafeFileHandle? device, out WinUsbNative.SafeWinUsbHandle? usb))
        {
            return false;
        }
        using (device)
        using (usb)
        {
            byte[] protocol = new byte[2];
            var query = new WinUsbNative.SetupPacket
            {
                RequestType = 0xc0,
                Request = 51,
                Length = 2
            };
            if (!WinUsbNative.WinUsbControlTransfer(usb!, query, protocol, 2, out uint read, 0) ||
                read != 2 || BitConverter.ToUInt16(protocol) < 1)
            {
                return false;
            }

            string[] values =
            [
                "ListenSphere",
                "ListenSphere Controller",
                "ListenSphere native USB audio transport",
                "1",
                "https://github.com/listen-sphere",
                serial
            ];
            for (ushort index = 0; index < values.Length; index++)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(values[index] + '\0');
                var send = new WinUsbNative.SetupPacket
                {
                    RequestType = 0x40,
                    Request = 52,
                    Index = index,
                    Length = checked((ushort)bytes.Length)
                };
                if (!WinUsbNative.WinUsbControlTransfer(
                    usb!, send, bytes, checked((uint)bytes.Length), out uint written, 0) ||
                    written != bytes.Length) return false;
            }

            var start = new WinUsbNative.SetupPacket { RequestType = 0x40, Request = 53 };
            return WinUsbNative.WinUsbControlTransfer(usb!, start, [], 0, out _, 0);
        }
    }

    private static bool TryInitialize(
        string path,
        out SafeFileHandle? device,
        out WinUsbNative.SafeWinUsbHandle? usb)
    {
        device = WinUsbNative.CreateFile(
            path,
            WinUsbNative.GenericRead | WinUsbNative.GenericWrite,
            WinUsbNative.FileShareRead | WinUsbNative.FileShareWrite,
            0,
            WinUsbNative.OpenExisting,
            0,
            0);
        usb = null;
        if (device.IsInvalid)
        {
            device.Dispose();
            device = null;
            return false;
        }
        if (!WinUsbNative.WinUsbInitialize(device, out WinUsbNative.SafeWinUsbHandle handle))
        {
            device.Dispose();
            device = null;
            handle?.Dispose();
            return false;
        }
        usb = handle;
        return true;
    }

    internal async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] chunk = new byte[destination.Length - offset];
            uint count = await Task.Run(() =>
            {
                if (!WinUsbNative.WinUsbReadPipe(
                    interfaceHandle, bulkIn, chunk, checked((uint)chunk.Length), out uint transferred, 0))
                {
                    throw WinUsbNative.Error("WinUSB 读取");
                }
                return transferred;
            }, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("WinUSB 通道已关闭。");
            chunk.AsSpan(0, checked((int)count)).CopyTo(destination.Span[offset..]);
            offset += checked((int)count);
        }
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = source.ToArray();
            uint count = await Task.Run(() =>
            {
                if (!WinUsbNative.WinUsbWritePipe(
                    interfaceHandle, bulkOut, bytes, checked((uint)bytes.Length), out uint transferred, 0))
                {
                    throw WinUsbNative.Error("WinUSB 写入");
                }
                return transferred;
            }, cancellationToken).ConfigureAwait(false);
            if (count != bytes.Length) throw new IOException("WinUSB 未写入完整数据帧。");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            interfaceHandle.Dispose();
            deviceHandle.Dispose();
            writeGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
