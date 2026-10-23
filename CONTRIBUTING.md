# Contributing to Nulluxion

Use [GitHub issues](https://github.com/aphrofluff/Nulluxion/issues) for bugs and focused feature proposals, and pull requests for changes. Read [SECURITY.md](SECURITY.md) before reporting a suspected vulnerability.

## Local development

Use Windows, Windows PowerShell 5.1, and .NET Framework 4.8. No package restore, SDK, or third-party libraries are needed. Follow the clone and build commands in [README.md](README.md), then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\check.ps1 -OutputDirectory .\dist\contributor-check
```

This compiles with warnings as errors, parses PowerShell, and runs isolated C# and script checks using fake monitor operations. It does not launch the production application or change physical monitor power or display configuration. The Windows workflow runs the same checks; a green check does not prove that a physical monitor powers off or wakes correctly.

Production C# files live at the repository root and are compiled together. Tests live in `tests` and use a separate entry point. Keep compatibility with the Windows .NET Framework compiler used by `build.ps1`; newer C# syntax and .NET-only APIs may be unavailable. Builds discover source files in sorted name order, ignore machine-local `csc.rsp`, and use explicit references and UTF-8 source encoding. The legacy compiler and Windows-provided drawing libraries do not guarantee byte-identical binaries across builds or machines.

## Changes and verification

- Keep changes focused; explain the concrete behavior before and after in the pull request.
- Add regression coverage for changed power, cancellation, recovery, or shortcut behavior through the existing injected backends. Automated tests must never call native power or display-changing APIs.
- Run `check.ps1` and report the command and result. Clearly identify checks you could not run.
- If physical testing is needed, follow the manual diagnostics section in the README. It is a separate, explicit step on suitable hardware; never add it to CI. Report monitor model, connection, Windows version, expected behavior, observed behavior, and restoration outcome. Review diagnostics for identifying information before attaching them.
- Do not commit generated executables, build output, logs, settings, or recovery records. Build in a separate output directory when a local copy is running.

Update documentation when behavior, commands, settings, or recovery procedures change. Do not change assembly/product naming or the legacy `%LOCALAPPDATA%\Blackout` storage paths without considering existing installations.

Source and documentation are licensed under **GPL-3.0-only**; see [LICENSE](LICENSE). Submit only work you have the right to contribute under that license.
