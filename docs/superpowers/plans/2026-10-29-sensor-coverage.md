# DevicePulse Sensor Compatibility Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline and request a final code review.

**Goal:** Improve disk SMART fallback and Intel/AMD/DDR identification in a real Windows build.
**Architecture:** Preserve current providers; add verified smartctl and model database as an independent background fallback. Merge only identity-matched valid data. Add firmware/WMI RAM reconciliation and documented CPU eligibility.
**Tech Stack:** .NET 10 / WinForms, LibreHardwareMonitor 0.9.6, DiskInfoToolkit 2.2.1, libcdi NWinfo 1.6.6, smartmontools 7.5.
**Spec:** docs/superpowers/specs/2026-10-29-sensor-coverage-design.md

## Global Constraints

- Windows x64, portable self-contained EXE; full RU/FR/EN.
- Preserve original signed PawnIO 2.2.0 verification and French consent UI.
- New separate 0.9.14 development build from last available 0.9.11 sources.
- No invented temperatures, HDD health percentages, DDR generations or disk identity.
- No physical probes, UAC acceptance, or driver installation in CI.

## Review Focus

- Failed SMART command with JSON must not become a healthy disk.
- Two identical drive models must remain separate by physical disk index.
- Integer overflow and malformed/duplicated JSON must be rejected.
- Missing firmware rows and ambiguous WMI slots must not invent RAM totals.
- CPU eligibility must track the installed library, including its disabled AMD legacy implementations.

## Tasks

### Task 1: Smartctl model-aware fallback

Files: overlays/src/SmartctlParser.cs, overlays/src/SmartctlStorage.cs, overlays/src/PerformanceStorage.cs; qa/CompatibilityTests.cs.
- [x] Add fixture tests for ATA/NVMe/SCSI, unknown data, command error bits, conflict handling, identity, counter overflow, and read-only argument construction. Run RED.
- [x] Implement strict JSON parser and integrity-checked bounded child process; enrich only confirmed physical disk/model matches.
- [x] Run fixture tests GREEN and integrate into full Windows QA.

### Task 2: CPU eligibility and RAM fallback

Files: overlays/src/CpuSensorSupport.cs, overlays/src/LibrarySensors.cs, overlays/src/CpuActivation.cs, overlays/src/PawnIoInstaller.cs, overlays/src/MemoryCompatibility.cs, overlays/src/PerformanceSensors.cs.
- [x] Add tests for Intel eligibility, supported AMD families, unsupported legacy AMD, DDR3/4/5 and LPDDR, unknown/contradictory generation and ambiguous memory module binding. Run RED.
- [x] Implement documented CPU eligibility and strict WMI memory fallback. Preserve direct Intel DTS validation.
- [x] Run tests GREEN.

### Task 3: Package, compile and verify

Files: .github/workflows/devicepulse.yml, docs/SENSOR-COMPATIBILITY.md, version/UI source overlays.
- [ ] Pin/check smartctl installer, x64 binary, GPL source archive and current compatible drivedb hash. Extract installer without executing it; include GPL license and corresponding source.
- [ ] Preserve synthetic non-elevated full UI QA, verify production admin manifest and bundle complete source.
- [ ] Run Windows Actions, review logs and artifacts, request final code review, and deliver actual compiled package with honest hardware limitations.
