Controller Toggle
=================

An Xbox Game Bar widget that turns your handheld's built-in controller off (and back on),
so docked play only sees your external controllers. Built for the Legion Go S; also
recognizes the Legion Go / Go 2, ROG Ally, MSI Claw and Zotac Zone, and falls back to
"the XInput controller Windows says is part of this PC" on other handhelds.

BEFORE YOU INSTALL: SMART APP CONTROL
  Smart App Control only runs apps that Microsoft's cloud service knows or that are
  signed by a publicly trusted certificate authority. This build is self-signed, so
  with Smart App Control on, Windows blocks the installer and the helper. Turn it off:
  Windows Security > App & browser control > Smart App Control settings > Off.
  (Since the April 2026 Windows update you can turn it back on later without
  reinstalling Windows, but Controller Toggle won't work while it's on. Turn the
  built-in controller on, or uninstall, before turning it back on.)

INSTALL
  Run ControllerToggle-Setup.exe (or Install.cmd from the zip) and approve the
  administrator prompt. It:
    1. trusts the widget's signing certificate (Local Machine > Trusted People),
    2. installs the Game Bar widget,
    3. installs the "Controller Toggle helper" Windows service.
  Then press Win+G (or your Game Bar button), open the Widgets menu, and pick
  "Controller Toggle". Pin it if you want it to stay on screen.

USE
  - "Turn off built-in controller" / "Turn on built-in controller" toggles it.
    A manual "off" stays off, even after a restart, until you turn it back on.
  - "Turn off automatically while an external controller is connected" (auto mode):
    plug in or pair a controller and the built-in one turns off; disconnect it and
    the built-in one comes back on by itself. Recommended for docking.

IF THE CONTROLLER STAYS OFF
  Use touch or a keyboard to open the widget and turn it back on, or run
  "C:\Program Files\ControllerToggle\Restore-Controller.cmd".
  Uninstalling also always turns it back on. If the helper can't run (for example,
  Smart App Control was turned back on), both fall back to Windows' own pnputil;
  Device Manager can always re-enable it too.

UNINSTALL
  Settings > Apps > Installed apps > Controller Toggle > Uninstall
  (or run Uninstall.cmd).

TROUBLESHOOTING
  Logs:     C:\ProgramData\ControllerToggle\helper.log and install.log
  Details:  open a terminal and run
            "C:\Program Files\ControllerToggle\ControllerToggleHelper.exe" --list
