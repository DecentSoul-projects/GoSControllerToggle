using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ControllerToggle.Helper.Native
{
    /// <summary>
    /// Calls back whenever an XInput or HID device interface appears or disappears
    /// (CM_Register_Notification, Windows 8+). Works from a service, no window needed.
    /// The callback runs on a system thread pool thread and must return quickly.
    /// </summary>
    internal sealed class DeviceChangeWatcher : IDisposable
    {
        private delegate uint CmNotifyCallback(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);

        private const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;

        // CM_NOTIFY_FILTER: 4 DWORDs, then a 400-byte union (GUID / HANDLE / WCHAR[200]). Total 416 bytes.
        [StructLayout(LayoutKind.Sequential)]
        private struct CM_NOTIFY_FILTER
        {
            public uint cbSize;
            public uint Flags;
            public uint FilterType;
            public uint Reserved;
            public Guid ClassGuid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 384)]
            public byte[] UnionPadding;
        }

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Register_Notification(ref CM_NOTIFY_FILTER pFilter, IntPtr pContext, CmNotifyCallback pCallback, out IntPtr pNotifyContext);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Unregister_Notification(IntPtr NotifyContext);

        private readonly Action _onChange;
        private readonly CmNotifyCallback _callback; // kept alive for the lifetime of the registrations
        private readonly List<IntPtr> _registrations = new List<IntPtr>();

        public DeviceChangeWatcher(Action onChange)
        {
            _onChange = onChange;
            _callback = OnNotification;
        }

        /// <summary>Registers for the given interface classes. Returns false if any registration failed.</summary>
        public bool Start(params Guid[] interfaceClasses)
        {
            bool allOk = true;
            foreach (Guid interfaceClass in interfaceClasses)
            {
                var filter = new CM_NOTIFY_FILTER
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(CM_NOTIFY_FILTER)),
                    Flags = 0,
                    FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                    Reserved = 0,
                    ClassGuid = interfaceClass,
                    UnionPadding = new byte[384]
                };

                IntPtr handle;
                int cr = CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out handle);
                if (cr == CfgMgr.CR_SUCCESS && handle != IntPtr.Zero)
                {
                    _registrations.Add(handle);
                }
                else
                {
                    Log.Warn("CM_Register_Notification failed for " + interfaceClass + " (CONFIGRET 0x" + cr.ToString("X") + ")");
                    allOk = false;
                }
            }
            return allOk;
        }

        private uint OnNotification(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
        {
            try
            {
                _onChange();
            }
            catch
            {
                // Never let an exception escape into native code.
            }
            return 0; // ERROR_SUCCESS
        }

        public void Dispose()
        {
            foreach (IntPtr handle in _registrations)
            {
                try
                {
                    CM_Unregister_Notification(handle);
                }
                catch
                {
                }
            }
            _registrations.Clear();
        }

        public static int FilterSize()
        {
            return Marshal.SizeOf(typeof(CM_NOTIFY_FILTER));
        }
    }
}
