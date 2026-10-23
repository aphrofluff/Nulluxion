# Nulluxion
Nulluxion is a Windows tray app that blacks out other screens so you can focus on one window, or blacks out every screen with a shortcut. It can also turn off compatible monitor backlights.

Formerly Blackout. The executable is now `Nulluxion.exe`; keyboard shortcuts are unchanged. Settings and monitor recovery records continue to use `%LOCALAPPDATA%\Blackout` for compatibility. The legacy startup registry value and single-instance identifier are retained. After replacing the executable, re-enable **Start Nulluxion when I sign in** in Settings to update the startup command to its new path.

## Quickstart from source

Use Windows with Windows PowerShell 5.1, Git, and .NET Framework 4.8. No .NET SDK, package restore, or third-party dependencies are needed. In PowerShell:

```powershell
git clone https://github.com/aphrofluff/Nulluxion.git
Set-Location Nulluxion
powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1
Start-Process .\dist\Nulluxion.exe
```

The check builds the application before running isolated tests. Launch the executable after the check succeeds; it appears in the Windows notification area. Open its tray menu for Settings, blackout actions, recovery, or Exit. Backlight control is enabled by default; turn off **Turn off LCD backlights when blacking out** in Settings if you want black overlays only.

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

Use Windows PowerShell 5.1 and .NET Framework 4.8. The build discovers the Framework64 compiler first, then the 32-bit Framework compiler, and treats compiler warnings as errors. Source files are sorted by name, read as UTF-8, and compiled with explicit references, ignoring machine-local compiler response files. The compiler path/version is printed for troubleshooting. The legacy compiler and Windows image-generation libraries do not guarantee byte-identical output across builds or machines.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` to build `dist\Nulluxion.exe`. Exit any running copy first. Use `-OutputDirectory C:\path\to\temporary-output` to build elsewhere without replacing the normal executable.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1` for the automated check: parse every PowerShell script, build production and test executables, run isolated C# tests, and run the hardware capability parser and tray smoke checks in fresh Windows PowerShell processes. The test executable is `dist\tests\Nulluxion.Tests.exe`; `check.ps1` also accepts `-OutputDirectory`. Relative output paths are resolved from the working directory. These checks use fake monitor operations and temporary recovery data; they do not launch the production app or change physical monitor power or display configuration.

The [Windows checks workflow](.github/workflows/windows.yml) is configured to run `check.ps1` on Windows Server 2022 for pushes, pull requests, and manual dispatch, with output in the runner's temporary directory. Its checkout action is pinned to a commit, with Dependabot configured to propose action updates. See [workflow runs](https://github.com/aphrofluff/Nulluxion/actions/workflows/windows.yml) for actual CI results. CI does not validate physical monitor behavior.

Build output consists of `Nulluxion.exe`, `nulluxion.ico`, `nulluxion.png`, and a copy of `LICENSE`; the images are generated by `build.ps1`. Include `LICENSE` when distributing the application, and satisfy the GPL's corresponding-source requirements. `check.ps1` also creates the test executable. Output under `dist` is generated and is not source material. Do not distribute diagnostic logs, settings, recovery records, or test binaries as application assets. The source root itself cannot be used as the output directory.

## Manual diagnostics

Use a fresh Windows PowerShell process for each diagnostic and build the executable first. Close Nulluxion before running diagnostics that change monitor power or display state. Those scripts refuse to proceed without `-AllowMonitorChanges` and while a Nulluxion/Blackout process is running. Have access to monitor power buttons and Windows display settings before starting; cleanup blocks attempt restoration but cannot recover from a terminated process or unresponsive monitor firmware. Do not delete recovery records to bypass a failed restoration; restore the monitors and layout, then use **Retire saved recovery...** as described above.

The isolated smoke check can run while Nulluxion is open: it injects no-op operations before tray construction, reads no settings or recovery data, registers no global hotkeys, and shows no overlays. Physical light output cannot be verified by software alone.

| Script | Purpose | Changes monitor power or display state? |
| --- | --- | --- |
| `hardware-test.ps1` | Capability parser cases against the built executable; accepts `-AssemblyPath`. | No |
| `hardware-probe.ps1` | Reports attached monitors and DDC/CI support. | No |
| `display-recovery-test.ps1` | Compiles and runs synthetic recovery tests with temporary records and fake display operations. | No |
| `smoke-test.ps1` | Isolated overlay properties and tray/shortcut state transitions with no-op monitor operations; accepts `-AssemblyPath`. | No |
| `display-wake-trace.ps1` | Observes Windows display on/off notifications and last-input timestamp changes for 180 seconds; accepts `-Seconds` and `-OutputPath`. Optional `-ClassifyInput` aggregates keyboard/mouse events by injection flags and mouse event kind. Records no key identities, text, or pointer positions. Can run alongside Nulluxion to investigate unexpected wakeups. | No |
| `hardware-integration-test.ps1` | Checks all-screen hardware power-off or Windows sleep fallback, cancellation, restoration, and supported-monitor DDC/CI off/on. | Yes |
| `display-sleep-test.ps1` | Suspends `-DeviceName` (defaults to DISPLAY2) for `-Seconds` (20 by default, 1-60 allowed) and checks desktop bounds and primary-monitor restoration. Verify the target first. | Yes |
| `benq-shortcut-test.ps1` | Setup-specific check of the app's BenQ path and restoration through shortcut and Escape; expects two monitors with DISPLAY1 kept on. | Yes |
| `benq-power-test.ps1` | Experimental manual BenQ power-command diagnostic. | Yes |

The power-changing scripts are opt-in and are never run by `check.ps1` or CI. For example, on a suitable setup after closing the app:

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\hardware-integration-test.ps1 -AllowMonitorChanges
```

For a read-only capability report, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\hardware-probe.ps1`. Review reports and logs before sharing; monitor identities, layout details, and local paths can identify your setup. The wake trace overwrites its `-OutputPath` on each run, so choose a new path to preserve an earlier trace.

## Architecture

`Nulluxion.cs` owns the tray application, settings, shortcuts, and blackout overlays. `HardwarePower.cs` coordinates monitor power work and DDC/CI capability checks and defines injectable power operations for isolated tests. `DisplaySleep.cs` handles display-configuration capture, signal suspension, and recovery. Production sources live in the repository root; isolated tests in `tests` compile alongside them with a separate test entry point.

## Contributing, security, and license

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and pull request guidance, and [SECURITY.md](SECURITY.md) for vulnerability reporting. Source and documentation are licensed under **GPL-3.0-only**; see [LICENSE](LICENSE).
