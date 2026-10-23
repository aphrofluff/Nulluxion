# Nulluxion

**Keep one screen in view. Darken the rest.**

Nulluxion is a lightweight Windows tray app for focusing on work, watching a video, or making your whole setup go dark. Use a keyboard shortcut to cover unwanted screens with black overlays and, on compatible monitors, turn off their backlights.

- **Focus on one screen:** keep the active window's monitor visible, or choose the monitor under your mouse pointer.
- **Black out everything:** darken every monitor, including a single-monitor setup.
- **Restore quickly:** press your shortcut again or **Escape**.
- **Make it yours:** customize the focus shortcut, enable sign-in startup, and choose overlays or hardware backlight control.

[Get started](#get-started) · [Shortcuts](#shortcuts) · [Settings](#settings) · [Monitor support](#monitor-support) · [Troubleshooting](#troubleshooting) · [Developer guide](#developer-guide)

## Get started

### Requirements

- **To run:** Windows with .NET Framework 4.8.
- **To build from source:** also install Git and use Windows PowerShell 5.1. No .NET SDK, package restore, or third-party libraries are needed.
- **For selective blackout:** connect at least two monitors. All-screen blackout works with one.
- **For backlight control:** compatible monitor hardware; enable **DDC/CI** in your monitor's on-screen menu where available. Black overlays work without DDC/CI.

### Build and launch

Open Windows PowerShell and run:

```powershell
git clone https://github.com/aphrofluff/Nulluxion.git
Set-Location Nulluxion
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
Start-Process .\dist\Nulluxion.exe
```

Launch the executable after the build succeeds. If you already have a built copy, open `Nulluxion.exe` directly.

### Your first blackout

1. Open Nulluxion. Settings opens on a normal launch, and a black icon appears in the Windows notification area, near the clock. Check the hidden-icons menu if needed.
2. Choose your preferences in Settings, then close the window. **Nulluxion keeps running in the tray.**
3. Activate the window you want to keep visible and press **Ctrl + Alt + B**. Your other screens go dark.
4. Press the same shortcut again or **Escape** to restore your screens.

Right-click the tray icon for blackout actions, Settings, recovery controls, **About / license**, or **Exit Nulluxion**. Double-click the icon to open Settings. Opening Settings restores an active blackout; exiting also attempts to restore monitor power.

**Backlight control is enabled by default.** For black overlays without hardware power or display-layout changes, turn off **Turn off LCD backlights when blacking out** in Settings.

## Shortcuts

| Action | Shortcut | What happens |
| --- | --- | --- |
| Focus on one screen | **Ctrl + Alt + B** | Keeps the active window's monitor visible and blacks out the others. Customizable in Settings. |
| Black out all screens | **Ctrl + Alt + Shift + B** | Includes the remaining visible screen. Works with one or more monitors. Fixed and reserved. |
| Restore screens | **Escape** or your focus shortcut | Removes overlays and requests monitor restoration. |
| Toggle all-screen blackout off | **Ctrl + Alt + Shift + B** again | Restores all screens when all-screen blackout is active. |

If you are already focusing on one screen, the all-screen shortcut darkens that screen too. Both blackout modes are also available from the tray menu.

Escape is registered only during blackout. If another app owns it, Nulluxion warns you; use your focus shortcut, the all-screen shortcut when in all-screen mode, or the tray restore action.

## Settings

Changes are saved as you make them.

| Setting | How to use it |
| --- | --- |
| **Toggle shortcut** | Click the shortcut field, then press your new combination. Use Ctrl, Alt, or Shift with a key, or an available function key. Escape, F12, and the all-screen shortcut are reserved. |
| **Start Nulluxion when I sign in** | Launches the current executable at sign-in. Keep it in a permanent location before enabling this. |
| **Keep the monitor with my mouse pointer** | Chooses the monitor under your pointer when you start selective blackout. When unchecked, the active window's monitor stays visible. |
| **Turn off LCD backlights when blacking out** | Enabled by default. Uses compatible hardware controls; turn it off for black overlays only. |
| **Retry restoration** | Requests another attempt to restore monitor power and the saved display configuration. |
| **Retire saved recovery...** | Archives unresolved recovery records after confirmation. Use only after restoring your monitors and layout manually; see [recovery](#a-monitor-stays-off-or-recovery-is-blocked). |

The hardware status area reports the latest power or recovery result. It is the first place to look when a monitor behaves differently than expected.

## Monitor support

### Black overlays

Nulluxion places borderless black windows over the selected screens without taking focus from your active window. Overlays hide the desktop; an LCD backlight may remain lit. Disable backlight control to use this mode without changing hardware power or display configuration.

### Backlight control

With backlight control enabled, the behavior depends on your monitors:

- **Compatible external monitors:** Nulluxion uses DDC/CI power commands. It checks advertised power support and confirms the monitor is on before changing it. In selective mode, unsupported monitors keep a black overlay.
- **BenQ EX240N:** Nulluxion suspends the display's video signal so its no-signal timeout can turn off the backlight. Restoration brings back the saved display modes, arrangement, and primary monitor. **Windows may move open windows while the signal is suspended.**
- **All screens:** Nulluxion first attempts hardware power-off. If every target can be powered off, ordinary mouse input does not wake them; use a Nulluxion shortcut or Escape. If a target is unsupported or fails, Nulluxion restores partially powered-off hardware before using Windows display sleep.

Windows display sleep waits until all keys and mouse buttons are released and input has been quiet for at least **800 ms**. Holding keys or moving the mouse postpones it. Later keyboard or mouse input can wake the displays; use a Nulluxion shortcut or Escape to remove any remaining overlays.

Hardware support depends on the monitor, firmware, and connection. Some monitors stop accepting software wake commands while off and require their physical power button. Others appear disconnected when powered off, so Windows may rearrange the desktop. Nulluxion does not change brightness or save power settings into monitor firmware.

## Troubleshooting

### I cannot find the app, or closing Settings did not quit it

Look for the black tray icon near the clock, including the hidden-icons menu. Nulluxion stays in the tray when Settings closes. Use **Exit Nulluxion** in its tray menu to quit. Only one copy can run at a time.

### A shortcut does not work

Another app may already have registered it. Choose a different **Toggle shortcut** in Settings. The all-screen shortcut cannot be changed; free **Ctrl + Alt + Shift + B**, then restart Nulluxion to register it again. The tray actions remain available if a shortcut conflicts.

### The wrong monitor stays visible

By default, Nulluxion selects the active window's monitor when blackout begins. Activate a window on the screen you want to keep. Alternatively, enable **Keep the monitor with my mouse pointer** and place your pointer on that screen before pressing the shortcut.

### The screen is black, but its backlight is still on

Check that backlight control is enabled and DDC/CI is enabled in the monitor's menu. Read the hardware status in Settings. Unsupported monitors use overlays in selective mode; the BenQ EX240N needs time for its no-signal timeout. You can also collect a [read-only capability report](#read-only-and-isolated-diagnostics).

### All screens wake immediately, or never go to sleep

The Windows display-sleep fallback reacts to input. Release every shortcut key and mouse button, then leave input quiet. Background or injected input can also postpone sleep or wake the displays. If they wake with overlays still present, press your focus shortcut or Escape. The [wake trace](#read-only-and-isolated-diagnostics) can help investigate recurring wakeups.

### A monitor stays off, or recovery is blocked

1. Press your focus shortcut or **Escape**, or use the tray's **Restore / retry monitor power** action. Settings also has **Retry restoration**.
2. If software wake fails, use the monitor's physical power button and restore the intended layout in Windows display settings.
3. If Nulluxion was interrupted, launch it again. It reads saved recovery records and retries restoration.
4. If recovery remains blocked **after your monitors and layout are restored manually**, choose **Retire saved recovery...** in the tray or Settings. Confirming retries restoration, then archives any remaining records so they no longer block new power changes.

Do not delete recovery files to bypass a failed restoration. Nulluxion preserves them when a monitor cannot be matched safely or the display layout has changed outside the app. See [local state and recovery](#local-state-and-recovery) for the technical details.

### I am upgrading from Blackout or moving the executable

The app was formerly named **Blackout**; the executable is now `Nulluxion.exe`. Existing shortcuts, settings, and recovery records are retained. Close the running copy before replacing it. After renaming or moving the executable, re-enable **Start Nulluxion when I sign in** to update the startup command to its current path.

### I need to report a problem

Open a [GitHub issue](https://github.com/aphrofluff/Nulluxion/issues) with your Windows version, monitor models, connection types, blackout mode, backlight setting, hardware status, and steps to reproduce. Explain what happened when you tried to restore the screens. Review diagnostics before sharing: monitor identities, layout details, and local paths can identify your setup.

For suspected vulnerabilities, follow [SECURITY.md](SECURITY.md).

---

## Developer guide

Nulluxion uses C#, Windows Forms, and native Windows display APIs, compiled directly with the .NET Framework compiler. Production C# files live in the repository root; isolated tests live in `tests/`. There is no solution file or package restore step.

[Build](#build) · [Checks and CI](#automated-checks-and-ci) · [Source map](#source-map) · [Power behavior](#power-behavior-and-cancellation) · [Recovery](#local-state-and-recovery) · [Diagnostics](#read-only-and-isolated-diagnostics) · [Contributing](#contributing-and-distribution)

### Build

From the repository root, use Windows PowerShell 5.1 on Windows with .NET Framework 4.8:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Exit any running copy before replacing its executable. To build alongside a running copy, choose a separate output directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory .\dist\dev
```

The default output is `dist/`:

| File | Purpose |
| --- | --- |
| `Nulluxion.exe` | Windows tray application. |
| `nulluxion.ico` and `nulluxion.png` | Black icon assets generated by the build; the icon is embedded in the executable. |
| `LICENSE` | License text copied for distribution. |

Both `build.ps1` and `check.ps1` accept `-OutputDirectory`. Relative paths resolve from your working directory; the source root itself cannot be the output directory.

The build discovers the 64-bit Framework compiler first, then the 32-bit compiler, and prints its path and version. It compiles sorted root-level C# sources as UTF-8 with explicit references, ignores machine-local `csc.rsp`, and treats warnings as errors. Keep changes compatible with that legacy compiler and .NET Framework APIs. Compiler and Windows image-generation differences mean builds are not guaranteed to be byte-identical.

### Automated checks and CI

Run the standard validation command before submitting a change:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1
```

For output separate from your normal build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1 -OutputDirectory .\dist\contributor-check
```

The check:

1. Parses every repository PowerShell script for syntax errors.
2. Builds the production application and `tests/Nulluxion.Tests.exe` under the selected output directory.
3. Runs isolated C# tests with fake monitor operations and temporary recovery data.
4. Runs the hardware capability parser and isolated tray smoke checks in fresh Windows PowerShell processes.

**These checks do not launch the production app or change physical monitor power or display configuration.** The tray smoke check uses no-op power operations, reads no user settings or recovery data, registers no global shortcuts, and shows no overlays. It can run while Nulluxion is open.

The [Windows checks workflow](.github/workflows/windows.yml) runs the same check on Windows Server 2022 for pushes, pull requests, and manual dispatch, with output in the runner's temporary directory. Checkout is pinned to a commit; [Dependabot](.github/dependabot.yml) is configured for action updates. See [workflow runs](https://github.com/aphrofluff/Nulluxion/actions/workflows/windows.yml) for CI results. A passing check does not verify physical light output or firmware wake behavior.

### Source map

| File or directory | Responsibility |
| --- | --- |
| [Nulluxion.cs](Nulluxion.cs) | Entry point, single-instance guard, tray UI, settings, global shortcuts, overlays, and Windows display-sleep input timing. |
| [HardwarePower.cs](HardwarePower.cs) | Background monitor-power worker, DDC/CI enumeration and capability parsing, cancellation, wake retries, and power recovery. Defines `IPowerController`, `IDisplayPower`, and injectable monitor operations. |
| [DisplaySleep.cs](DisplaySleep.cs) | Display-configuration capture, BenQ signal suspension, layout comparison, recovery records, and injectable display operations. |
| [AssemblyInfo.cs](AssemblyInfo.cs) | Application and assembly metadata. |
| [tests/](tests/) | Hardware-worker, display-recovery, and tray-state tests, compiled with production sources and a separate test entry point. |
| [build.ps1](build.ps1) / [check.ps1](check.ps1) | Build entry point and automated validation. |
| [.github/](.github/) | Windows CI and dependency-update configuration. |

### Power behavior and cancellation

Monitor commands run on a background worker so shortcuts and tray actions remain responsive. Requests are versioned: cancellation or a newer target set prevents stale work from completing as the current request. Failed DDC wake operations are retried.

**Selective blackout:** overlays cover every screen except the selected one. Compatible external monitors use `SetVCPFeature` with VCP feature `D6`. Eligibility requires advertised values `01` (on) and `04` (off), plus confirmation that the monitor is currently on. Nulluxion sends the write-only off value `05` if also advertised, otherwise `04`, and restores with `01`. Unsupported targets keep their overlays.

**BenQ EX240N:** signal suspension uses Windows display-configuration APIs. Direct DDC power-off was observed to disconnect this model's controller, while standby commands left its backlight on, so the app uses the signal path instead. At least one display must remain active for signal suspension.

**All-screen blackout:** the worker suspends eligible BenQ signals first, then refreshes monitor identities and handles from active displays before issuing DDC commands. If any target cannot be powered off, it restores partial hardware changes before the UI requests Windows sleep through `SC_MONITORPOWER`. Sleep is gated on completed restoration, released keys/buttons, and at least 800 ms of quiet input. Later input may wake Windows sleep; the app does not repeatedly force displays back to sleep.

Power, session, and external display changes request restoration. Expected topology changes during BenQ signal sleep are handled specially so they do not cancel that blackout.

### Local state and recovery

The legacy data directory is **`%LOCALAPPDATA%\Blackout`**, retained for compatibility:

| File | Contents |
| --- | --- |
| `settings.xml` | Focus shortcut, monitor-selection preference, and backlight preference. |
| `power-recovery.txt` | DDC monitor identities requiring restoration. New records use stable PnP identities. |
| `display-recovery.bin` | Saved display configuration for signal suspension. Version 2 stores both the original and expected suspended configurations. |

Startup uses the `Blackout` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing to the current executable with `--startup`. The single-instance identifier also retains its legacy name. Consider existing installations before changing any of these identifiers or paths.

Recovery is written before power or display changes. The next launch retries interrupted restoration. The recovery logic also protects layouts and identities:

- **External layout changes:** if the configuration no longer matches the expected suspended layout, automatic restoration of the old layout is blocked to preserve the newer arrangement.
- **Rebooted display adapters:** a record is cleared if the complete current configuration already matches the saved original apart from reassigned adapter identifiers. Legacy display records are retired automatically only when the current configuration is equivalent.
- **Legacy DDC records:** old `DISPLAYn` names cannot reliably identify the same monitor after a reboot. They are retained for explicit retirement rather than guessed.
- **Explicit retirement:** after manual restoration, **Retire saved recovery...** retries restoration and archives remaining records. Archived records are no longer retried automatically.

Successful display restoration removes `display-recovery.bin`; DDC restoration removes recovered monitor entries from `power-recovery.txt`, which may remain as an empty file. Unreadable or unresolved records can block new hardware power changes; preserve them and follow the [user recovery steps](#a-monitor-stays-off-or-recovery-is-blocked).

### Read-only and isolated diagnostics

Use a fresh Windows PowerShell process for each diagnostic. Build first for scripts that load `dist/Nulluxion.exe`; scripts with `-AssemblyPath` can target another build.

| Script | Purpose |
| --- | --- |
| [hardware-probe.ps1](hardware-probe.ps1) | Read-only attached-monitor and DDC/CI capability report. |
| [hardware-test.ps1](hardware-test.ps1) | Capability-parser cases against a built executable; accepts `-AssemblyPath`. |
| [display-recovery-test.ps1](display-recovery-test.ps1) | Compiles current sources and runs synthetic recovery tests with fake display operations and temporary records. |
| [smoke-test.ps1](smoke-test.ps1) | Isolated overlay properties and tray/shortcut state transitions; accepts `-AssemblyPath`. |
| [display-wake-trace.ps1](display-wake-trace.ps1) | Observes display on/off notifications and last-input timestamp changes. Can run alongside Nulluxion. |

Collect capabilities without changing monitor power:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\hardware-probe.ps1
```

Capture a wake trace, optionally classifying input events:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\display-wake-trace.ps1 -Seconds 180 -ClassifyInput -OutputPath .\dist\diagnostics\wake-trace.log
```

The trace defaults to 180 seconds and accepts `-Seconds` and `-OutputPath`. `-ClassifyInput` aggregates keyboard/mouse events by injection flags and mouse event kind; it records no key identities, text, or pointer positions. Each run overwrites its output path, so choose a new filename to preserve an earlier trace. Review all reports and logs before sharing.

### Physical monitor diagnostics

These scripts change real monitor power or display configuration. They are separate from automated checks and **never run in `check.ps1` or CI**.

Close Nulluxion first. Each script refuses to proceed without **`-AllowMonitorChanges`** or while a Nulluxion/Blackout process is running. Have access to monitor power buttons and Windows display settings. Cleanup attempts restoration, but cannot guarantee recovery from a terminated process or unresponsive firmware.

| Script | Purpose and prerequisites |
| --- | --- |
| [hardware-integration-test.ps1](hardware-integration-test.ps1) | Exercises all-screen hardware-off or Windows-sleep fallback, cancellation, restoration, and supported-monitor DDC off/on. |
| [display-sleep-test.ps1](display-sleep-test.ps1) | Suspends `-DeviceName` (default `\\.\DISPLAY2`) for `-Seconds` (default 20; range 1–60), then checks display bounds and primary-monitor restoration. Verify the target first. |
| [benq-shortcut-test.ps1](benq-shortcut-test.ps1) | Setup-specific BenQ blackout and shortcut/Escape restoration check. Expects two monitors with `DISPLAY1` kept on. |
| [benq-power-test.ps1](benq-power-test.ps1) | Experimental BenQ EX240N standby-command diagnostic. |

For a suitable setup, after closing the app:

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\hardware-integration-test.ps1 -AllowMonitorChanges
```

Physical light output requires observation. Report the monitor model, connection, Windows version, expected and observed behavior, and restoration outcome. If restoration fails, keep the recovery records and follow the [recovery steps](#a-monitor-stays-off-or-recovery-is-blocked).

### Contributing and distribution

Read [CONTRIBUTING.md](CONTRIBUTING.md) for change and pull request guidance. Cover power, cancellation, recovery, and shortcut changes through the existing injected backends; automated tests must not call native APIs that change monitor power or display configuration.

`dist/` is generated output. Do not commit generated binaries, icons, logs, settings, or recovery records. Distribute application assets with `LICENSE`; omit test binaries and local diagnostics. Follow the GPL's corresponding-source requirements when distributing the application.

Source and documentation are licensed under **GPL-3.0-only**; see [LICENSE](LICENSE). For vulnerability reporting, see [SECURITY.md](SECURITY.md).
