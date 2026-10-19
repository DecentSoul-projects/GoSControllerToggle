using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ControllerToggle.Helper.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    /// <summary>Device property keys from devpkey.h.</summary>
    internal static class DevPropKeys
    {
        private static readonly Guid DeviceFmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        public static readonly DEVPROPKEY DeviceDesc = new DEVPROPKEY(DeviceFmtid, 2);
        public static readonly DEVPROPKEY HardwareIds = new DEVPROPKEY(DeviceFmtid, 3);
        public static readonly DEVPROPKEY Service = new DEVPROPKEY(DeviceFmtid, 6);
        public static readonly DEVPROPKEY ClassGuid = new DEVPROPKEY(DeviceFmtid, 10);
        public static readonly DEVPROPKEY CompatibleIds = new DEVPROPKEY(DeviceFmtid, 4);
        public static readonly DEVPROPKEY ConfigFlags = new DEVPROPKEY(DeviceFmtid, 12);
        public static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY(DeviceFmtid, 14);
        public static readonly DEVPROPKEY Address = new DEVPROPKEY(DeviceFmtid, 30);
        public static readonly DEVPROPKEY BusReportedDeviceDesc = new DEVPROPKEY(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
        public static readonly DEVPROPKEY ContainerId = new DEVPROPKEY(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
        public static readonly DEVPROPKEY InstanceId = new DEVPROPKEY(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    }

    /// <summary>Device interface class GUIDs.</summary>
    internal static class InterfaceGuids
    {
        /// <summary>GUID_DEVINTERFACE_XUSB: exposed by every XInput-capable device (xusb22.sys / xinputhid.sys).</summary>
        public static readonly Guid XUsb = new Guid("ec87f1e3-c13b-4100-b5f7-8b84d54260cb");

        /// <summary>GUID_DEVINTERFACE_HID.</summary>
        public static readonly Guid Hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        /// <summary>GUID_DEVINTERFACE_USB_HUB.</summary>
        public static readonly Guid UsbHub = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    }

    /// <summary>
    /// Thin wrappers over the Configuration Manager API (cfgmgr32.dll). These are read-only
    /// queries that work for standard users too; only enable/disable needs admin (see SetupApi).
    /// </summary>
    internal static class CfgMgr
    {
        public const int CR_SUCCESS = 0x00;
        public const int CR_NO_SUCH_DEVNODE = 0x0D;
        public const int CR_BUFFER_SMALL = 0x1A;
        public const int CR_NO_SUCH_VALUE = 0x25;

        public const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
        public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x0;

        public const uint DN_STARTED = 0x00000008;
        public const uint DN_HAS_PROBLEM = 0x00000400;
        public const uint DN_NEED_RESTART = 0x01000000;
        public const uint CM_PROB_DISABLED = 0x16;
        public const uint CONFIGFLAG_DISABLED = 0x00000001;

        public const uint DEVPROP_TYPE_UINT32 = 0x07;
        public const uint DEVPROP_TYPE_GUID = 0x0D;
        public const uint DEVPROP_TYPE_STRING = 0x12;
        public const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

        private const int MaxDeviceIdLength = 200;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint dnDevInst, [Out] char[] Buffer, int BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType, [Out] byte[] PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string pszFilter, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid InterfaceClassGuid, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_ListW(ref Guid InterfaceClassGuid, string pDeviceID, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DEVPROPKEY PropertyKey, out uint PropertyType, [Out] byte[] PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

        public static bool TryLocate(string instanceId, out uint devInst)
        {
            devInst = 0;
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }
            return CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
        }

        public static bool TryGetParent(uint devInst, out uint parent)
        {
            return CM_Get_Parent(out parent, devInst, 0) == CR_SUCCESS;
        }

        public static string GetDeviceId(uint devInst)
        {
            var buffer = new char[MaxDeviceIdLength + 1];
            if (CM_Get_Device_IDW(devInst, buffer, buffer.Length, 0) != CR_SUCCESS)
            {
                return null;
            }
            int length = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, length < 0 ? buffer.Length : length);
        }

        public static bool TryGetStatus(uint devInst, out uint status, out uint problem)
        {
            return CM_Get_DevNode_Status(out status, out problem, devInst, 0) == CR_SUCCESS;
        }

        private static byte[] GetPropertyBytes(uint devInst, DEVPROPKEY key, out uint type)
        {
            type = 0;
            uint size = 0;
            int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
            if (cr != CR_BUFFER_SMALL || size == 0)
            {
                return null;
            }
            var buffer = new byte[size];
            cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
            return cr == CR_SUCCESS ? buffer : null;
        }

        public static string GetStringProperty(uint devInst, DEVPROPKEY key)
        {
            uint type;
            byte[] data = GetPropertyBytes(devInst, key, out type);
            if (data == null || type != DEVPROP_TYPE_STRING)
            {
                return null;
            }
            return DecodeString(data);
        }

        public static string[] GetStringListProperty(uint devInst, DEVPROPKEY key)
        {
            uint type;
            byte[] data = GetPropertyBytes(devInst, key, out type);
            if (data == null || type != DEVPROP_TYPE_STRING_LIST)
            {
                return new string[0];
            }
            return SplitMultiSz(Encoding.Unicode.GetString(data)).ToArray();
        }

        public static Guid? GetGuidProperty(uint devInst, DEVPROPKEY key)
        {
            uint type;
            byte[] data = GetPropertyBytes(devInst, key, out type);
            if (data == null || type != DEVPROP_TYPE_GUID || data.Length < 16)
            {
                return null;
            }
            var bytes = new byte[16];
            Buffer.BlockCopy(data, 0, bytes, 0, 16);
            return new Guid(bytes);
        }

        public static uint? GetUInt32Property(uint devInst, DEVPROPKEY key)
        {
            uint type;
            byte[] data = GetPropertyBytes(devInst, key, out type);
            if (data == null || type != DEVPROP_TYPE_UINT32 || data.Length < 4)
            {
                return null;
            }
            return BitConverter.ToUInt32(data, 0);
        }

        /// <summary>Instance IDs of devices, optionally limited to one enumerator ("USB", "HID", ...).</summary>
        public static List<string> GetDeviceIds(string enumerator, bool presentOnly)
        {
            uint flags = presentOnly ? CM_GETIDLIST_FILTER_PRESENT : 0;
            if (!string.IsNullOrEmpty(enumerator))
            {
                flags |= CM_GETIDLIST_FILTER_ENUMERATOR;
            }
            else
            {
                enumerator = null;
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint length;
                if (CM_Get_Device_ID_List_SizeW(out length, enumerator, flags) != CR_SUCCESS || length == 0)
                {
                    return new List<string>();
                }
                var buffer = new char[length];
                int cr = CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
                if (cr == CR_SUCCESS)
                {
                    return SplitMultiSz(new string(buffer));
                }
                if (cr != CR_BUFFER_SMALL)
                {
                    break;
                }
            }
            return new List<string>();
        }

        /// <summary>Paths of present device interfaces of the given class (optionally only for one device).</summary>
        public static List<string> GetInterfaces(Guid interfaceClass, string deviceInstanceId)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint length;
                Guid g = interfaceClass;
                if (CM_Get_Device_Interface_List_SizeW(out length, ref g, deviceInstanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || length <= 1)
                {
                    return new List<string>();
                }
                var buffer = new char[length];
                int cr = CM_Get_Device_Interface_ListW(ref g, deviceInstanceId, buffer, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (cr == CR_SUCCESS)
                {
                    return SplitMultiSz(new string(buffer));
                }
                if (cr != CR_BUFFER_SMALL)
                {
                    break;
                }
            }
            return new List<string>();
        }

        /// <summary>The instance ID of the device that owns a device interface path.</summary>
        public static string GetInterfaceOwnerInstanceId(string interfacePath)
        {
            DEVPROPKEY key = DevPropKeys.InstanceId;
            uint type;
            uint size = 0;
            int cr = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out type, null, ref size, 0);
            if (cr != CR_BUFFER_SMALL || size == 0)
            {
                return null;
            }
            var buffer = new byte[size];
            cr = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out type, buffer, ref size, 0);
            if (cr != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
            {
                return null;
            }
            return DecodeString(buffer);
        }

        private static string DecodeString(byte[] data)
        {
            string s = Encoding.Unicode.GetString(data);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        internal static List<string> SplitMultiSz(string multiSz)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(multiSz))
            {
                return result;
            }
            foreach (string part in multiSz.Split('\0'))
            {
                if (part.Length > 0)
                {
                    result.Add(part);
                }
            }
            return result;
        }
    }
}
