# Controller Toggle

An Xbox Game Bar widget that turns a Windows handheld's **built-in controller** off and back on, so
when you dock it, games only see your external controllers. Made for the **Lenovo Legion Go S**;
also recognizes the Legion Go / Go 2, ROG Ally, MSI Claw and Zotac Zone, and falls back to "the
XInput controller Windows says is part of this PC" on other handhelds.

- **Toggle** the built-in controller from Game Bar (touch, mouse or an external gamepad).
- **Auto mode** (optional): plug in or pair a controller and the built-in one turns off; disconnect
  it and the built-in one comes back on by itself.
- **One installer** that trusts the signing certificate, installs the widget and installs the helper.
- **Safe to undo**: stopping or uninstalling the helper turns the controller back on, and
  `Restore-Controller.cmd` fixes it even if everything else is broken.

```
 Xbox Game Bar                                     Windows
 ┌────────────────────────────┐   named pipe    ┌───────────────────────────────┐
 │ Controller Toggle widget   │  "get" "enable" │ ControllerToggle service      │
 │ (UWP, sandboxed, no admin) ├────────────────►│ (LocalSystem)                 │
 │                            │◄────────────────┤  • finds the built-in pad     │
 └────────────────────────────┘   JSON state    │  • SetupDi enable / disable   │
                                                │  • watches for external pads  │
                                                └───────────────────────────────┘
```

## Build

You need Windows and **Visual Studio 2022** (Community is fine) with the C# UWP tools: in
*Visual Studio Installer > Modify*, tick the **WinUI application development** workload (called
*Windows application development* in 17.10-17.12, *Universal Windows Platform development* before
that), then under *Installation details* tick **Universal Windows Platform tools**. Then
double-click **`Build.cmd`**.

It finds Visual Studio and the Windows SDK, creates a self-signed signing certificate in
`signing\` (first run only), builds the helper and the widget, and writes:

- `dist\ControllerToggle-Setup.exe`: single-file installer (a small launcher with the files embedded; asks for admin once)
- `dist\ControllerToggle-Setup.zip`: the same files; run `Install.cmd` inside

No Visual Studio? Push the folder to a GitHub repository and run the **Build** workflow
(`.github/workflows/build.yml`); download the installer from the run's artifacts.

Keep `signing\` private: anything signed with that key is trusted on PCs where you installed
Controller Toggle.

## Install (on the handheld)

**Turn off Smart App Control first** if it's on (*Windows Security > App & browser control >
Smart App Control settings*). It only runs code that Microsoft's cloud service recognizes or that
is signed by a certificate authority in Microsoft's Trusted Root Program, so it blocks this
self-signed build: the installer, and the helper service after it. Since the April 2026 Windows
update it can be turned back on later without reinstalling Windows, but Controller Toggle won't
work while it's on (turn the built-in controller back on before you re-enable it). To ship a build
that works with Smart App Control on, sign it with a publicly trusted certificate (for example
Microsoft's Artifact Signing) instead of the self-signed one.

Run `ControllerToggle-Setup.exe` and approve the administrator prompt. It:

1. trusts the widget's certificate in **Local Machine > Trusted People** (what Windows needs to
   sideload an MSIX; nothing is added to the Root store),
2. installs the widget package and its framework dependencies (`Add-AppxPackage`),
3. copies the helper to `C:\Program Files\ControllerToggle` and registers the
   **Controller Toggle helper** service (automatic start, restarts if it crashes),
4. adds **Controller Toggle** to *Settings > Apps* for uninstalling.

Then press **Win+G** (or the handheld's Game Bar button), open **Widgets**, and choose
**Controller Toggle**. Pin it to keep it on screen.

## How it works

### Turning the controller off: same mechanism as Handheld Hardware Tools

HHT's "toggle controller" (`Classes/ControllerStatus_Management.cs`) uses Device Manager's
disable path, SetupDi `DIF_PROPERTYCHANGE` with `DICS_DISABLE` and `DICS_FLAG_GLOBAL`, on
the device node that exposes the XInput interface (`GUID_DEVINTERFACE_XUSB`), then power-cycles
its USB port. The helper does the same, with two changes:

- **It targets the right device.** HHT disables whichever XInput device it happens to enumerate
  first, without checking VID/PID, so with an external pad connected it can pick the wrong one.
  This helper looks for the node of a known handheld controller (Go S: `USB\VID_1A86&PID_E310`)
  that runs the Xbox 360 driver (`xusb22`); on a Go S that's interface `MI_01`, while `MI_00`
  is its keyboard. Matching by driver rather than interface number also finds it while it's
  disabled, and never touches the touchpad, keyboard or vendor interfaces. Other XInput devices
  count as built-in if their parent chain contains a known handheld's VID/PID, or failing that
  if Windows puts them in the "part of this PC" container.
- **It only power-cycles the port when needed.** If an app such as Steam has the controller
  open, Windows can't stop it right away and reports "restart required". HHT always cycles
  the USB port to finish the job; this helper does it only in that case
  (`IOCTL_USB_HUB_CYCLE_PORT`).

The disabled state is stored by Windows, so a manual "off" survives a reboot (like Device
Manager / HHT). Auto mode re-evaluates at every start.

GoTweaks and HandheldCompanion use HidHide to *hide* the controller instead, which needs a
third-party driver. Disabling needs nothing extra.

### Widget ↔ helper: GoTweaks' pipe approach, simpler hosting

A Game Bar widget is a sandboxed UWP app (AppContainer, Low integrity) and can't change
devices. GoTweaks pairs its widget with an elevated helper over a named pipe whose ACL grants
`ALL APPLICATION PACKAGES`. Its widget opens the pipe with a raw `CreateFileW`, because UWP's
`NamedPipeClientStream` can't. Controller Toggle does the same, plus:

- the pipe has a **Low mandatory label** (`S:(ML;;NW;;;LW)`), the documented way to let a
  Low-integrity client write to a higher-integrity pipe, and rejects remote clients;
- the helper is a **Windows service** registered by the installer, instead of GoTweaks'
  "copy the helper to %LocalAppData% and create an elevated scheduled task on first launch" scheme
  (its source carries many fixes for setup races and zombie processes). The service runs from
  admin-only `Program Files`, starts with Windows, and needs no UAC prompts after install;
- the widget closes its connection when Game Bar suspends it, and the service drops idle or
  stuck clients (both are GoTweaks lessons: suspended widgets never close their end on their own).

The protocol is one word per line (`get`, `enable`, `disable`, `auto on`, `auto off`); the
service answers with one JSON line and pushes a new one whenever something changes. It accepts
nothing else, so the worst another app on the PC could do through the pipe is flip this toggle.

### Auto mode

The service listens for XInput and HID device arrivals and removals (`CM_Register_Notification`).
An **external controller** is any XInput device or HID game controller (`HID_DEVICE_SYSTEM_GAME`)
that isn't part of a known handheld, isn't in the PC's own device container, and isn't
software-created (ViGEm / VIIPER virtual pads sit under `ROOT\`, so they're ignored). Devices
are grouped by container ID, so one pad that shows up as several nodes counts once.

When the first external controller connects, the built-in one turns off; when the last one
disconnects, it turns back on. A manual toggle in between sticks until the next connect or
disconnect. Turning auto mode off returns to your manual setting.

## Troubleshooting

- **"Smart App Control blocked an app that may be unsafe":** see *Install* above; the installer
  also stops with that explanation if it's started from the zip while Smart App Control is on.
- **Widget says the helper isn't reachable:** if Smart App Control is on, it's blocking the helper.
  Otherwise check the service with `sc query ControllerToggle`; reinstall or restart.
- **See what the helper detects:** `"C:\Program Files\ControllerToggle\ControllerToggleHelper.exe" --list`
  (`--selftest` also checks the native structure layouts).
- **Logs:** `C:\ProgramData\ControllerToggle\helper.log` and `install.log`.
- **Controller stuck off:** `Restore-Controller.cmd` in the install folder, or uninstall. Both fall
  back to Windows' own `pnputil /enable-device` if the helper can't run; Device Manager works too.
- **Note:** an Xbox 360 wireless receiver counts as an external controller while it's plugged in.

## Project layout

```
Build.cmd                  double-click to build
build\Build.ps1            the build (VS + SDK detection, certificate, compile, package, Setup.exe)
src\Helper\                the service (.NET Framework 4.8, x64; also a CLI: --list --status --restore ...)
  Core\                    detection (DeviceScanner), policy (ToggleCore, AutoPolicy), settings, log
  Native\                  cfgmgr32 / SetupAPI / USB hub interop, device notifications
  Ipc\PipeServer.cs        the named pipe server (+ the CLI's client)
src\Widget\                the Game Bar widget (UWP, Microsoft.Gaming.XboxGameBar 7.3)
installer\                 Install / Uninstall / Restore scripts that go into the setup
  SetupLauncher\           the single-file Setup.exe (unpacks the embedded zip, runs Install.ps1)
.github\workflows\         optional cloud build
```

## Credits

- **Handheld Hardware Tools** (project-sbc): the SetupDi disable + USB port-cycle technique.
- **GoTweaks** (corando98): the AppContainer-to-elevated-helper named pipe and `CreateFileW`
  client technique, the installer flow (`TrustedPeople` + `Add-AppxPackage`), and the Legion Go S
  gamepad interface IDs.
- **HandheldCompanion** (Valkirie): handheld controller VID/PIDs.
- **Microsoft XboxGameBarSamples** (MIT): widget project structure, activation code and the
  Game Bar proxy-stub manifest entries.

All code here is new; only the Game Bar sample's project scaffolding was adapted.
