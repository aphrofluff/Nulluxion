# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'))
$ErrorActionPreference = 'Stop'
# AssemblyPath remains accepted for compatibility. Compile the current implementation
# with an injected synthetic backend: this test never calls native display APIs.
# Retains the stale-LUID regression and covers multi-adapter layouts, corruption,
# external topology, rollback, and archive failure.
$sourceFiles = @('DisplaySleep.cs', 'HardwarePower.cs', 'tests\DisplaySleepTests.cs')
$paths = @($sourceFiles | ForEach-Object { Join-Path $PSScriptRoot $_ })
Add-Type -Path $paths -IgnoreWarnings
[DisplaySleepTests]::Run()

