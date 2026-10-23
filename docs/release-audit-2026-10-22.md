# Source publication audit — 2026-10-22

The audited working tree is ready for GitHub source publication under GPL-3.0-only. No unresolved publication-blocking code findings remain from this review. This is a source-readiness assessment, not a claim of compatibility with every monitor or driver.

## Scope and changes

Astra reviewed application lifecycle, monitor power, native interop, display topology, and recovery behavior. Sol handled build scripts, CI, documentation, and repository hygiene. Independent cross-reviews checked the fixes; the additional signal-suspension verification finding was fixed and re-reviewed.

- **Display recovery:** validate persisted native mode references, identities, dimensions, duplicate paths/modes, and structure layouts; reject unreadable records; detect topology changes before suspension; guard coordinate arithmetic against overflow. Verify the physical target set after suspension and the complete configuration after restoration. Preserve recovery when a driver reports success without performing the requested change or verification fails.
- **Monitor power:** release fresh monitor handles when wake operations throw; contain native enumeration callback exceptions; reject incomplete clone-group identities; preserve unreadable journals on failed archival. The worker now owns its synchronization-event cleanup through shutdown, including callback disposal and bounded join timeouts.
- **Application lifecycle and settings:** restore hardware and remove overlays even when a wake operation throws; perform idempotent cleanup through both disposal and application exit; retain power-worker ownership through startup failures. Bound XML settings input, prohibit DTDs, and validate persisted shortcuts. Invalid shortcuts reset without losing unrelated preferences.
- **Release materials:** add the full GPL-3.0 license, source notices, an About/license tray action, contribution instructions, and a security-reporting policy. Include the license with builds. Build with explicit references and UTF-8 source input, compiler warnings as errors, and no machine-local compiler response file.
- **Automation:** add a Windows Server 2022 GitHub Actions workflow with read-only repository permissions, a commit-pinned checkout action, and Dependabot action updates. Physical diagnostics require explicit opt-in and a closed application.
- **Repository hygiene:** remove all 21 previously tracked `dist` files from the Git index while preserving the local files. Ignore generated builds, diagnostics, and common local configuration artifacts. Generated icons require no checked-in binary assets.

## Verification

All of the following passed locally on Windows using the .NET Framework compiler reported as `14.8.10001.0`:

- Production and isolated test builds with warnings treated as errors.
- **103 hardware worker assertions, 82 display recovery assertions, and 54 tray/settings assertions** (239 C# assertions).
- **13 capability-parser cases**, isolated tray/overlay smoke checks, and parsing of all **11 PowerShell scripts**.
- The complete `check.ps1` command from a fresh export containing only Git source candidates, with no `.git` directory or pre-existing build files. Both source and output paths contained spaces; execution started outside the source directory.
- A separate `/platform:x86` build and execution of all 239 C# assertions, in addition to the normal AnyCPU run on this 64-bit machine.
- Git whitespace checks; distribution-license hash comparison; local documentation links; CI YAML parsing with duplicate-key rejection; checkout pin verification against upstream; and ignore-rule probes.

The usual command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1 -OutputDirectory .\dist\release-check
```

All behavioral tests used injected monitor/display backends. No production application launch, physical monitor-power command, or display-configuration change was performed during the audit.

## History and privacy

A bounded custom scan covered 8 reachable commits (2 reachable from the local `origin/main` ref and 6 checkpoint-only commits), 53 unique blobs totaling 440,858 bytes, commit metadata, and the 27 source/documentation candidates present at scan time. It found no credential-pattern candidates, absolute user paths, or stable monitor-instance identifiers in scanned content. This was not a security certification; unreachable/reflog-only objects, unfetched remote refs, and ignored local files were outside that scan.

The existing Git history still contains a 420-byte historical display snapshot (two paths, four modes, locally unique adapter identifiers), display/input activity timestamps, ordinary settings fields, and an empty power-recovery file. Normal author/committer email metadata also remains. Removing files from the current tree does not erase historical copies. No history rewrite was performed. A source-only export omits that history; publishing the existing Git history retains it.

## Verification limits

- Remote GitHub Actions has been configured but has not run for these changes; no commit or push was made.
- Actual backlight behavior, firmware wake support, and driver restoration require manual checks on the intended hardware. The README describes those opt-in diagnostics and recovery procedures.
- Native driver calls may block; shutdown waits are bounded, and recovery records support another launch if work cannot finish before process exit.
- Generated executables are unsigned. This audit prepares source publication; it does not establish a signed binary release or a physical compatibility matrix.

Source and documentation are licensed under GPL-3.0-only; see [LICENSE](../LICENSE).
