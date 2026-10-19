using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ControllerToggle.Helper.Native
{
    /// <summary>
    /// Power-cycles the USB hub port a device is plugged into (IOCTL_USB_HUB_CYCLE_PORT).
    /// HHT does this right after disabling the controller: if an app such as Steam has the
    /// controller open, Windows can't stop it immediately and only marks it "disable on
    /// restart". Cycling the port makes the device re-enumerate, and it comes back already
    /// disabled. We only do it when Windows reports that a restart would be needed.
    /// </summary>
    internal static class UsbPort
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x00220444;

        [StructLayout(LayoutKind.Sequential)]
        private struct USB_CYCLE_PORT_PARAMS
        {
            public uint ConnectionIndex;
            public uint StatusReturned;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref USB_CYCLE_PORT_PARAMS lpInBuffer, uint nInBufferSize, ref USB_CYCLE_PORT_PARAMS lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        /// <summary>
        /// Cycles the port of the USB device that <paramref name="instanceId"/> belongs to
        /// (walking up from an interface node such as ...&amp;MI_00 to the USB device itself).
        /// </summary>
        public static bool TryCycle(string instanceId, out string message)
        {
            message = "";
            uint devInst;
            if (!CfgMgr.TryLocate(instanceId, out devInst))
            {
                message = "device not found";
                return false;
            }

            // Find the USB device node (USB\VID_xxxx&PID_xxxx\serial, no &MI_ interface suffix).
            string id = CfgMgr.GetDeviceId(devInst);
            for (int depth = 0; depth < 5 && id != null && !IsUsbDeviceNode(id); depth++)
            {
                uint parent;
                if (!CfgMgr.TryGetParent(devInst, out parent))
                {
                    id = null;
                    break;
                }
                devInst = parent;
                id = CfgMgr.GetDeviceId(devInst);
            }

            if (id == null || !IsUsbDeviceNode(id))
            {
                message = "couldn't find the USB device for " + instanceId;
                return false;
            }

            uint? port = CfgMgr.GetUInt32Property(devInst, DevPropKeys.Address);
            if (!port.HasValue || port.Value == 0)
            {
                message = "couldn't read the USB port number of " + id;
                return false;
            }

            uint hub;
            if (!CfgMgr.TryGetParent(devInst, out hub))
            {
                message = "couldn't find the USB hub of " + id;
                return false;
            }

            string hubId = CfgMgr.GetDeviceId(hub);
            List<string> hubPaths = CfgMgr.GetInterfaces(InterfaceGuids.UsbHub, hubId);
            if (hubPaths.Count == 0)
            {
                message = "the USB hub " + hubId + " has no hub interface";
                return false;
            }

            using (SafeFileHandle handle = CreateFileW(hubPaths[0], GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    message = "opening the USB hub failed: " + ChangeResult.DescribeError(Marshal.GetLastWin32Error());
                    return false;
                }

                var input = new USB_CYCLE_PORT_PARAMS { ConnectionIndex = port.Value, StatusReturned = 0 };
                var output = new USB_CYCLE_PORT_PARAMS();
                uint returned;
                uint size = (uint)Marshal.SizeOf(typeof(USB_CYCLE_PORT_PARAMS));
                if (!DeviceIoControl(handle, IOCTL_USB_HUB_CYCLE_PORT, ref input, size, ref output, size, out returned, IntPtr.Zero))
                {
                    message = "cycling USB port " + port.Value + " failed: " + ChangeResult.DescribeError(Marshal.GetLastWin32Error());
                    return false;
                }
            }

            message = "cycled USB port " + port.Value + " of " + hubId;
            return true;
        }

        private static bool IsUsbDeviceNode(string id)
        {
            return id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                && id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0
                && id.IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
