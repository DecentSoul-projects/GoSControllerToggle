using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ControllerToggle.Helper.Native
{
    internal sealed class ChangeResult
    {
        public bool Success;
        public bool NeedsRestart;
        public int Win32Error;
        public string Message;

        public static ChangeResult Fail(string step, int error)
        {
            return new ChangeResult
            {
                Success = false,
                Win32Error = error,
                Message = step + " failed: " + DescribeError(error)
            };
        }

        public static string DescribeError(int error)
        {
            uint code = unchecked((uint)error);
            switch (code)
            {
                case 0x5: return "access denied (0x5) - the helper must run as administrator";
                case 0xE000020B: return "device not found (0xE000020B)";
                case 0xE0000231: return "Windows doesn't allow this device to be disabled (0xE0000231)";
                case 0xE0000219: return "no device information set (0xE0000219)";
            }
            string text;
            try
            {
                text = new Win32Exception(error).Message;
            }
            catch
            {
                text = "error";
            }
            return text + " (0x" + code.ToString("X8") + ")";
        }
    }

    /// <summary>
    /// Enables / disables a device the same way Device Manager, devcon, pnputil and
    /// Handheld Hardware Tools do: DIF_PROPERTYCHANGE with DICS_ENABLE / DICS_DISABLE and
    /// DICS_FLAG_GLOBAL. Requires administrator rights. The disabled state is stored by
    /// Windows and survives reboots until the device is enabled again.
    /// </summary>
    internal static class SetupApi
    {
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private const uint DIF_PROPERTYCHANGE = 0x12;
        private const uint DICS_ENABLE = 1;
        private const uint DICS_DISABLE = 2;
        private const uint DICS_FLAG_GLOBAL = 1;
        private const uint DI_NEEDRESTART = 0x00000080;
        private const uint DI_NEEDREBOOT = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        // 64-bit layout (setupapi.h uses 8-byte packing on 64-bit Windows). The helper is x64-only.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SP_DEVINSTALL_PARAMS
        {
            public uint cbSize;
            public uint Flags;
            public uint FlagsEx;
            public IntPtr hwndParent;
            public IntPtr InstallMsgHandler;
            public IntPtr InstallMsgHandlerContext;
            public IntPtr FileQueue;
            public UIntPtr ClassInstallReserved;
            public uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DriverPath;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DEVINSTALL_PARAMS DeviceInstallParams);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        public static ChangeResult SetEnabled(string instanceId, bool enable)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == InvalidHandleValue || set == IntPtr.Zero)
            {
                return ChangeResult.Fail("SetupDiCreateDeviceInfoList", Marshal.GetLastWin32Error());
            }

            try
            {
                var devInfo = new SP_DEVINFO_DATA();
                devInfo.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref devInfo))
                {
                    return ChangeResult.Fail("Opening the device", Marshal.GetLastWin32Error());
                }

                var change = new SP_PROPCHANGE_PARAMS();
                change.ClassInstallHeader.cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
                change.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
                change.StateChange = enable ? DICS_ENABLE : DICS_DISABLE;
                change.Scope = DICS_FLAG_GLOBAL;
                change.HwProfile = 0;

                if (!SetupDiSetClassInstallParamsW(set, ref devInfo, ref change, (uint)Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))))
                {
                    return ChangeResult.Fail("Preparing the state change", Marshal.GetLastWin32Error());
                }

                if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref devInfo))
                {
                    return ChangeResult.Fail(enable ? "Enabling the device" : "Disabling the device", Marshal.GetLastWin32Error());
                }

                bool needsRestart = false;
                var installParams = new SP_DEVINSTALL_PARAMS();
                installParams.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINSTALL_PARAMS));
                if (SetupDiGetDeviceInstallParamsW(set, ref devInfo, ref installParams))
                {
                    needsRestart = (installParams.Flags & (DI_NEEDRESTART | DI_NEEDREBOOT)) != 0;
                }

                return new ChangeResult { Success = true, NeedsRestart = needsRestart, Message = "" };
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>Sanity check for the struct layouts this file depends on (x64 values).</summary>
        public static string CheckLayouts()
        {
            int devInfo = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            int change = Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS));
            int install = Marshal.SizeOf(typeof(SP_DEVINSTALL_PARAMS));
            bool ok = devInfo == 32 && change == 20 && install == 584;
            return (ok ? "ok" : "UNEXPECTED") + " (SP_DEVINFO_DATA=" + devInfo + ", SP_PROPCHANGE_PARAMS=" + change + ", SP_DEVINSTALL_PARAMS=" + install + ")";
        }
    }
}
