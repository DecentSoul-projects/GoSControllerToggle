using System;
using System.Collections.Generic;
using ControllerToggle.Helper.Native;

namespace ControllerToggle.Helper
{
    internal enum BuiltInState
    {
        Missing,
        Enabled,
        Disabled,
        PendingRestart,
        NotWorking
    }

    /// <summary>A handheld whose built-in controller has a known USB VID/PID.</summary>
    internal sealed class KnownController
    {
        public readonly string Name;
        public readonly ushort VendorId;
        public readonly ushort[] ProductIds;

        public KnownController(string name, ushort vendorId, ushort[] productIds)
        {
            Name = name;
            VendorId = vendorId;
            ProductIds = productIds;
        }

        public bool Matches(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }
            foreach (ushort pid in ProductIds)
            {
                string tag = "VID_" + VendorId.ToString("X4") + "&PID_" + pid.ToString("X4");
                if (instanceId.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal static class KnownControllers
    {
        // IDs cross-checked against HandheldCompanion (Devices/*) and GoTweaks
        // (ControllerSuppressionManager). The Legion Go S is VID 1A86 / PID E310 (XInput mode) or
        // E311 (DInput mode). Which USB interface carries the gamepad depends on the firmware
        // (MI_01 on a 2026 unit; MI_00 there is the keyboard), so the gamepad node is found by its
        // driver (xusb22), never by interface number. The other interfaces (touchpad, keyboard,
        // vendor HID) are left alone.
        public static readonly KnownController[] All =
        {
            new KnownController("Legion Go S controller", 0x1A86, new ushort[] { 0xE310, 0xE311 }),
            new KnownController("Legion Go controller", 0x17EF, new ushort[] { 0x6182, 0x6183, 0x6184, 0x6185 }),
            new KnownController("Legion Go 2 controller", 0x17EF, new ushort[] { 0x61EB, 0x61EC, 0x61ED, 0x61EE }),
            new KnownController("ROG Ally controller", 0x0B05, new ushort[] { 0x1ABE, 0x1B4C }),
            new KnownController("MSI Claw controller", 0x0DB0, new ushort[] { 0x1901, 0x1902, 0x1903 }),
            new KnownController("Zotac Zone controller", 0x1EE9, new ushort[] { 0x1590 }),
        };

        public static KnownController Match(string instanceId)
        {
            foreach (KnownController known in All)
            {
                if (known.Matches(instanceId))
                {
                    return known;
                }
            }
            return null;
        }

        public static KnownController MatchLineage(List<string> lineage)
        {
            foreach (string id in lineage)
            {
                KnownController known = Match(id);
                if (known != null)
                {
                    return known;
                }
            }
            return null;
        }
    }

    /// <summary>A device node plus the facts we need to classify it.</summary>
    internal sealed class DeviceNode
    {
        public string InstanceId;
        public uint DevInst;

        /// <summary>This node's instance ID followed by its ancestors' (up to, not including, HTREE\ROOT).</summary>
        public readonly List<string> Lineage = new List<string>();

        public Guid? ContainerId;
        public string Name;

        /// <summary>Created by software (ViGEm, VIIPER, other root/software-enumerated buses).</summary>
        public bool IsVirtual;

        /// <summary>Windows groups it with the PC itself (built-in hardware).</summary>
        public bool InLocalMachineContainer;

        /// <summary>Belongs to a known handheld controller (matched anywhere in the lineage).</summary>
        public KnownController Known;

        /// <summary>The device's driver is running (DN_STARTED). Paired-but-off Bluetooth pads can stay "present" without this.</summary>
        public bool IsStarted;

        public string GroupKey
        {
            get { return ContainerId.HasValue ? ContainerId.Value.ToString("B") : InstanceId; }
        }
    }

    internal sealed class BuiltInController
    {
        public DeviceNode Node;
        public string DisplayName;
        public string Reason;
        public BuiltInState State;
        public bool DisabledFlag;
        public uint Status;
        public uint Problem;
        public uint ConfigFlags;
    }

    internal sealed class ExternalController
    {
        public string Key;
        public string Name;
        public string InstanceId;
        public string Kind;
    }

    internal sealed class ScanResult
    {
        public BuiltInController BuiltIn;
        public readonly List<ExternalController> Externals = new List<ExternalController>();
        public readonly List<DeviceNode> XInputDevices = new List<DeviceNode>();
        public readonly List<DeviceNode> HidGameDevices = new List<DeviceNode>();
        public readonly List<string> Notes = new List<string>();
    }

    internal sealed class DeviceScanner
    {
        private static readonly Guid LocalMachineContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");

        private sealed class Candidate
        {
            public DeviceNode Node;
            public int Priority;
            public string DisplayName;
            public string Reason;
        }

        /// <summary>
        /// Finds the built-in controller and the external controllers that are connected.
        /// Read-only: works without admin rights.
        /// </summary>
        public ScanResult Scan(string persistedTarget, bool includeExternals)
        {
            var result = new ScanResult();

            // Every enabled XInput device exposes GUID_DEVINTERFACE_XUSB (USB via xusb22, Bluetooth
            // Xbox pads via xinputhid). A disabled device has no interfaces, so this list never
            // contains a controller we've turned off.
            var seenXInput = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in CfgMgr.GetInterfaces(InterfaceGuids.XUsb, null))
            {
                string owner = CfgMgr.GetInterfaceOwnerInstanceId(path);
                if (owner == null || !seenXInput.Add(owner))
                {
                    continue;
                }
                DeviceNode node = Describe(owner);
                if (node != null)
                {
                    result.XInputDevices.Add(node);
                }
            }

            result.BuiltIn = FindBuiltIn(result, persistedTarget);

            if (includeExternals)
            {
                FindExternals(result);
            }

            return result;
        }

        private BuiltInController FindBuiltIn(ScanResult result, string persistedTarget)
        {
            var candidates = new List<Candidate>();

            // 1. The xusb22 (Xbox 360 driver) node of a known handheld's USB controller, e.g.
            //    USB\VID_1A86&PID_E310&MI_01\... on the Legion Go S. The driver and class stay on a
            //    disabled node (unlike its XInput interface), so this also finds it while it's off.
            foreach (string id in CfgMgr.GetDeviceIds("USB", true))
            {
                KnownController known = KnownControllers.Match(id);
                if (known == null)
                {
                    continue;
                }
                DeviceNode node = Describe(id);
                if (node != null && !node.IsVirtual && IsXusbNode(node.DevInst))
                {
                    candidates.Add(new Candidate { Node = node, Priority = 0, DisplayName = known.Name, Reason = "known " + known.Name + " gamepad interface" });
                }
            }

            // 2. XInput devices that belong to a known handheld, or that Windows says are part of this PC.
            //    (Fixes HHT's approach, which took whichever XInput device happened to be listed first.)
            foreach (DeviceNode node in result.XInputDevices)
            {
                if (node.IsVirtual)
                {
                    continue;
                }
                if (node.Known != null)
                {
                    candidates.Add(new Candidate { Node = node, Priority = 1, DisplayName = node.Known.Name, Reason = "XInput device of a known handheld (" + node.Known.Name + ")" });
                }
                else if (node.InLocalMachineContainer)
                {
                    candidates.Add(new Candidate { Node = node, Priority = 2, DisplayName = "Built-in controller", Reason = "XInput device that Windows reports as part of this PC" });
                }
            }

            // 3. The device we managed last time (needed for generic devices while they're disabled).
            if (!string.IsNullOrEmpty(persistedTarget) && !ContainsInstance(candidates, persistedTarget))
            {
                DeviceNode node = Describe(persistedTarget);
                if (node != null && !node.IsVirtual && (node.Known != null || node.InLocalMachineContainer))
                {
                    candidates.Add(new Candidate
                    {
                        Node = node,
                        Priority = 3,
                        DisplayName = node.Known != null ? node.Known.Name : "Built-in controller",
                        Reason = "the controller this helper managed before"
                    });
                }
            }

            Candidate best = null;
            foreach (Candidate c in candidates)
            {
                if (best == null || c.Priority < best.Priority)
                {
                    best = c;
                }
            }

            if (best == null)
            {
                result.Notes.Add("No built-in controller found.");
                return null;
            }

            if (CountDistinct(candidates) > 1)
            {
                result.Notes.Add(CountDistinct(candidates) + " built-in candidates; using " + best.Node.InstanceId);
            }

            return DescribeBuiltIn(best.Node, best.DisplayName, best.Reason);
        }

        private void FindExternals(ScanResult result)
        {
            var externals = new Dictionary<string, ExternalController>(StringComparer.OrdinalIgnoreCase);
            string builtInId = result.BuiltIn != null ? result.BuiltIn.Node.InstanceId : null;

            foreach (DeviceNode node in result.XInputDevices)
            {
                if (IsExternal(node, builtInId))
                {
                    AddExternal(externals, node, "XInput");
                }
            }

            // HID game controllers (DualSense, DualShock 4, Switch Pro, 8BitDo in D-mode, Bluetooth
            // Xbox pads, ...). The HID class driver gives joystick/gamepad collections the
            // compatible ID HID_DEVICE_SYSTEM_GAME.
            foreach (string id in CfgMgr.GetDeviceIds("HID", true))
            {
                uint devInst;
                if (!CfgMgr.TryLocate(id, out devInst) || !IsGameHid(devInst))
                {
                    continue;
                }
                DeviceNode node = Describe(id);
                if (node == null)
                {
                    continue;
                }
                result.HidGameDevices.Add(node);
                if (IsExternal(node, builtInId))
                {
                    AddExternal(externals, node, "HID");
                }
            }

            result.Externals.AddRange(externals.Values);
        }

        private static bool IsExternal(DeviceNode node, string builtInId)
        {
            if (node.IsVirtual || node.Known != null || node.InLocalMachineContainer || !node.IsStarted)
            {
                return false;
            }
            if (builtInId != null)
            {
                foreach (string id in node.Lineage)
                {
                    if (string.Equals(id, builtInId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void AddExternal(Dictionary<string, ExternalController> externals, DeviceNode node, string kind)
        {
            string key = node.GroupKey;
            if (externals.ContainsKey(key))
            {
                return;
            }
            externals[key] = new ExternalController
            {
                Key = key,
                Name = string.IsNullOrEmpty(node.Name) ? "Controller" : node.Name,
                InstanceId = node.InstanceId,
                Kind = kind
            };
        }

        private static bool IsGameHid(uint devInst)
        {
            foreach (string id in CfgMgr.GetStringListProperty(devInst, DevPropKeys.CompatibleIds))
            {
                if (IsGameHidId(id))
                {
                    return true;
                }
            }
            foreach (string id in CfgMgr.GetStringListProperty(devInst, DevPropKeys.HardwareIds))
            {
                if (IsGameHidId(id))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsGameHidId(string id)
        {
            return id.Equals("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase)
                || id.Equals("HID_DEVICE_UP:0001_U:0004", StringComparison.OrdinalIgnoreCase)
                || id.Equals("HID_DEVICE_UP:0001_U:0005", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsInstance(List<Candidate> candidates, string instanceId)
        {
            foreach (Candidate c in candidates)
            {
                if (string.Equals(c.Node.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountDistinct(List<Candidate> candidates)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate c in candidates)
            {
                ids.Add(c.Node.InstanceId);
            }
            return ids.Count;
        }

        /// <summary>Reads a device node's lineage, container and name. Null if the node isn't present.</summary>
        public static DeviceNode Describe(string instanceId)
        {
            uint devInst;
            if (!CfgMgr.TryLocate(instanceId, out devInst))
            {
                return null;
            }

            var node = new DeviceNode { InstanceId = instanceId, DevInst = devInst };
            node.Lineage.Add(instanceId);
            var lineageDevInsts = new List<uint> { devInst };

            uint current = devInst;
            for (int depth = 0; depth < 32; depth++)
            {
                uint parent;
                if (!CfgMgr.TryGetParent(current, out parent))
                {
                    break;
                }
                string parentId = CfgMgr.GetDeviceId(parent);
                if (parentId == null || parentId.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                node.Lineage.Add(parentId);
                lineageDevInsts.Add(parent);
                current = parent;
            }

            // Real hardware hangs off a PCI or ACPI node (USB host controller, Bluetooth radio,
            // I2C controller). Software-created pads (ViGEm, VIIPER, ...) sit under ROOT\ or SWD\
            // buses with no hardware above them. (Don't just look for a ROOT\ ancestor: every
            // device descends from ROOT\ACPI_HAL.)
            node.IsVirtual = true;
            foreach (string id in node.Lineage)
            {
                if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
                {
                    node.IsVirtual = false;
                    break;
                }
            }

            uint status;
            uint problem;
            node.IsStarted = CfgMgr.TryGetStatus(devInst, out status, out problem) && (status & CfgMgr.DN_STARTED) != 0;

            node.ContainerId = CfgMgr.GetGuidProperty(devInst, DevPropKeys.ContainerId);
            node.InLocalMachineContainer = node.ContainerId.HasValue && node.ContainerId.Value == LocalMachineContainer;
            node.Known = KnownControllers.MatchLineage(node.Lineage);
            node.Name = ReadName(lineageDevInsts);
            return node;
        }

        private static string ReadName(List<uint> lineageDevInsts)
        {
            // The product string the device itself reports is the most recognizable name
            // ("Xbox Wireless Controller", "DualSense Wireless Controller"); the INF description
            // of xusb22 nodes is just "Xbox 360 Controller for Windows".
            for (int i = 0; i < lineageDevInsts.Count && i < 3; i++)
            {
                string name = CfgMgr.GetStringProperty(lineageDevInsts[i], DevPropKeys.BusReportedDeviceDesc);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            string friendly = CfgMgr.GetStringProperty(lineageDevInsts[0], DevPropKeys.FriendlyName);
            if (!string.IsNullOrWhiteSpace(friendly))
            {
                return friendly.Trim();
            }
            string desc = CfgMgr.GetStringProperty(lineageDevInsts[0], DevPropKeys.DeviceDesc);
            return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        }

        public static BuiltInController DescribeBuiltIn(DeviceNode node, string displayName, string reason)
        {
            var b = new BuiltInController { Node = node, DisplayName = displayName, Reason = reason };
            uint status;
            uint problem;
            bool haveStatus = CfgMgr.TryGetStatus(node.DevInst, out status, out problem);
            uint? configFlags = CfgMgr.GetUInt32Property(node.DevInst, DevPropKeys.ConfigFlags);

            b.Status = haveStatus ? status : 0;
            b.Problem = haveStatus ? problem : 0;
            b.ConfigFlags = configFlags.HasValue ? configFlags.Value : 0;

            bool problemDisabled = haveStatus && (status & CfgMgr.DN_HAS_PROBLEM) != 0 && problem == CfgMgr.CM_PROB_DISABLED;
            b.DisabledFlag = (b.ConfigFlags & CfgMgr.CONFIGFLAG_DISABLED) != 0 || problemDisabled;
            bool started = haveStatus && (status & CfgMgr.DN_STARTED) != 0;

            if (b.DisabledFlag)
            {
                b.State = started ? BuiltInState.PendingRestart : BuiltInState.Disabled;
            }
            else
            {
                b.State = started ? BuiltInState.Enabled : BuiltInState.NotWorking;
            }
            return b;
        }

        /// <summary>All present nodes that look like a known handheld gamepad interface (used by --restore).</summary>
        public static List<string> KnownGamepadNodes()
        {
            var ids = new List<string>();
            foreach (string id in CfgMgr.GetDeviceIds("USB", true))
            {
                uint devInst;
                if (KnownControllers.Match(id) != null && CfgMgr.TryLocate(id, out devInst) && IsXusbNode(devInst))
                {
                    ids.Add(id);
                }
            }
            return ids;
        }

        private static readonly Guid XnaCompositeClass = new Guid("d61ca365-5af4-4486-998b-9db4734c6ca3");

        /// <summary>True for a node driven by xusb22 (class "Xbox 360 Peripherals"). Works while it's disabled.</summary>
        public static bool IsXusbNode(uint devInst)
        {
            string service = CfgMgr.GetStringProperty(devInst, DevPropKeys.Service);
            if (string.Equals(service, "xusb22", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Guid? cls = CfgMgr.GetGuidProperty(devInst, DevPropKeys.ClassGuid);
            return cls.HasValue && cls.Value == XnaCompositeClass;
        }

        public static string StateName(BuiltInController builtIn)
        {
            if (builtIn == null)
            {
                return "missing";
            }
            switch (builtIn.State)
            {
                case BuiltInState.Enabled: return "enabled";
                case BuiltInState.Disabled: return "disabled";
                case BuiltInState.PendingRestart: return "pending-restart";
                case BuiltInState.NotWorking: return "not-working";
                default: return "missing";
            }
        }
    }
}
