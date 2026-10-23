# Nulluxion
Formerly Blackout. The executable is now `Nulluxion.exe`; the existing keyboard shortcuts are unchanged.

Settings and monitor recovery records continue to use `%LOCALAPPDATA%\Blackout` for compatibility. The legacy startup registry value and single-instance identifier are also retained. After replacing the executable, re-enable **Start Nulluxion when I sign in** in Settings to update the startup command to its new path. GitHub repository URLs remain unchanged until the repositories are renamed.

an app that allows you to black out all of your screens but one, so that way you can focus in on the work or a video. 

## Shortcuts

- **Ctrl + Alt + B**: black out every monitor except the active window's monitor (configurable in Settings).
- **Ctrl + Alt + Shift + B**: black out **all** monitors, including on a single-monitor computer. This shortcut is fixed and reserved.
- Press the all-screen shortcut again, the original shortcut, or **Escape** to restore all monitors. If already blacking out other monitors, the all-screen shortcut adds the remaining screen.

The tray menu also offers both modes. Settings displays the all-screen shortcut. If another app owns that shortcut, Nulluxion reports the conflict; the tray action remains available.

## LCD backlights

**Turn off LCD backlights when blacking out** is enabled by default in Settings. Disable it to use black overlays only.

- **Other screens:** sends DDC/CI power-off to compatible external monitors, keeping the active monitor on. Enable DDC/CI in your monitor's on-screen menu. Nulluxion checks that the monitor advertises power-on and power-off support and confirms it is currently on before changing it. Unsupported monitors retain a black overlay.
- **BenQ EX240N:** automatically suspends its video signal instead. The monitor's no-signal timeout turns off its backlight; pressing the shortcut again or Escape restores the original display modes, arrangement, and primary monitor. Windows may move open windows while the display is suspended. This model's direct DDC power-off disconnects its controller, and its standby commands were observed to leave it on, so Nulluxion does not use those commands for it.
- **All screens:** first tries direct DDC/CI power-off and BenQ signal suspension for every display. If every target can power off, the monitors stay off until a Nulluxion shortcut, Escape, or tray restore action; ordinary mouse input does not wake them. If any target is unsupported or fails, Nulluxion restores partially powered-off hardware before falling back to Windows display sleep. The fallback waits until all keys and mouse buttons are released and input has remained quiet for at least 800 ms. Holding a shortcut key or moving the mouse postpones sleep. Later keyboard or mouse input, including periodic background input, can wake Windows display sleep; press a Nulluxion shortcut or Escape to remove the overlays.
- The shortcut, Escape, tray **Restore / retry monitor power**, and Exit restore monitors. Power/session/display changes also request restoration, except the expected topology changes during BenQ signal sleep.
- Monitor commands run in a background worker so shortcuts and the tray remain responsive. Failed DDC wake commands are retried. A recovery record in `%LOCALAPPDATA%\Blackout\power-recovery.txt` lets the next launch retry restoration after an interrupted session. New DDC recovery records identify monitors by stable PnP identity. Older records using `DISPLAYn` names cannot be safely matched automatically and remain available for explicit retirement.
- BenQ signal sleep saves both the original and expected suspended display configurations in a version 2 record at `%LOCALAPPDATA%\Blackout\display-recovery.bin` before changing the display. Recovery detects external topology changes, preserves the newer layout, and blocks automatic restoration of the old layout. Successful restoration removes this record.
- After a reboot, Windows can assign new display adapter identifiers. If a saved recovery record already matches the current complete display configuration apart from those identifiers, Nulluxion clears the obsolete record so selective backlight control can work again. Legacy display records are automatically retired only when the current configuration is already equivalent.
- If recovery remains blocked, manually restore your monitors and layout, then use **Retire saved recovery...** in the tray or Settings. After confirmation, Nulluxion retries restoration and archives any remaining records so a saved session no longer blocks new power changes.

Settings shows the latest hardware result. Some monitor firmware stops accepting DDC/CI while powered off; if software wake fails, use its power button. Some connections may report a powered-off monitor as disconnected, causing Windows to adjust the display layout. Hardware support determines these behaviors; black-overlay mode avoids hardware power changes.

Implementation uses [`SetVCPFeature`](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature) with VCP D6 for compatible monitors. Eligibility requires advertised values `01` (on) and `04` (off); Nulluxion sends the write-only off value `05` when also advertised, otherwise `04`, and restores with `01`. All-screen hardware power-off suspends BenQ signals first, then refreshes monitor identities and handles from active displays before issuing DDC commands. Display-configuration changes provide BenQ signal suspension, and [`SC_MONITORPOWER`](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand) provides the all-screen Windows sleep fallback. Nulluxion never changes brightness or saves power settings into monitor firmware.

## Build and check

Requires Windows with .NET Framework 4.x (4.8 recommended). No package restore or third-party dependencies are needed. The build discovers the Framework64 compiler first, then the 32-bit Framework compiler, and treats compiler warnings as errors.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` to build `dist\Nulluxion.exe`. Exit any running copy first. Use `-OutputDirectory C:\path\to\temporary-output` to build elsewhere without replacing the normal executable.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1` for the automated check: parse every PowerShell script, build production and test executables, run isolated C# tests, and run the hardware capability parser and tray smoke checks in fresh Windows PowerShell processes. The test executable is `dist\tests\Nulluxion.Tests.exe`; `check.ps1` also accepts `-OutputDirectory`. These checks use fake monitor operations and temporary recovery data; they do not change physical monitor power or display configuration. Windows CI runs the same command.

## Manual diagnostics

Close Nulluxion before running diagnostics that change monitor power or display state. The isolated smoke check can run while Nulluxion is open: it injects no-op operations before tray construction, reads no settings or recovery data, registers no global hotkeys, and shows no overlays. Physical light output cannot be verified by software alone.

| Script | Purpose | Changes monitor power or display state? |
| --- | --- | --- |
| `hardware-test.ps1` | Capability parser cases against the built executable; accepts `-AssemblyPath`. | No |
| `hardware-probe.ps1` | Reports attached monitors and DDC/CI support. | No |
| `display-recovery-test.ps1` | Compiles and runs synthetic recovery tests with temporary records and fake display operations. | No |
| `smoke-test.ps1` | Isolated overlay properties and tray/shortcut state transitions with no-op monitor operations; accepts `-AssemblyPath`. | No |
| `display-wake-trace.ps1` | Observes Windows display on/off notifications and last-input timestamp changes for 180 seconds; accepts `-Seconds` and `-OutputPath`. Optional `-ClassifyInput` aggregates keyboard/mouse events by injection flags and mouse event kind. Records no key identities, text, or pointer positions. Can run alongside Nulluxion to investigate unexpected wakeups. | No |
| `hardware-integration-test.ps1` | Checks all-screen hardware power-off or Windows sleep fallback, cancellation, restoration, and supported-monitor DDC/CI off/on. | Yes |
| `display-sleep-test.ps1` | Suspends DISPLAY2 for 20 seconds and checks desktop bounds and primary-monitor restoration. | Yes |
| `benq-shortcut-test.ps1` | Checks the app's BenQ path and restoration through shortcut and Escape. | Yes |
| `benq-power-test.ps1` | Experimental manual BenQ power-command diagnostic. | Yes |

The power-changing scripts are opt-in and are never run by `check.ps1` or CI.

## Architecture

`Nulluxion.cs` owns the tray application, settings, shortcuts, and blackout overlays. `HardwarePower.cs` coordinates monitor power work and DDC/CI capability checks and defines injectable power operations for isolated tests. `DisplaySleep.cs` handles display-configuration capture, signal suspension, and recovery. Production sources live in the repository root; isolated tests in `tests` compile alongside them with a separate test entry point.
