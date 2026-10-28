# DevicePulse — stable build reference

## Latest owner-designated stable binary: **DevicePulse 0.9.13 Admin**

Windows x64 portable executable; the embedded application manifest requests **administrator privileges** at startup (`requireAdministrator`). Full reference details and exact SHA-256 verification: [DevicePulse 0.9.13 Admin](releases/v0.9.13-admin/RELEASE.md).

**Caution:** this repository's existing source and earlier GitHub releases are *not* matching 0.9.13 Admin source. The stable executable and the portable ZIP have not yet been uploaded as GitHub release assets. No report export is present in this stable version.

---

## BatteryDiagnostic 0.8.0 — historical repository base

Portable Windows x64, .NET 10 bundled. Extract source.zip and apply overrides/src before building.

0.8.0 adds installed RAM from the complete firmware module inventory, separately labels usable Windows RAM, and shows per-drive CrystalDiskInfo/libcdi integration status. The native library is pinned to official NWinfo v1.6.6, SHA-256-verified at build time and before loading, and loaded only beside the application. Status distinguishes missing/rejected DLL, load or read failure, unmatched disks, matched data and stale samples. SMART details retain diagnostics. Existing trustworthy readings are retained when CDI provides fallback data; disk identity requires both physical drive index and model.

CI checks native DLL exports and reads cdi_get_version without creating a SMART object or enumerating hardware. A separate non-elevated fixture harness tests parsers, RAM accounting, CDI state reporting, disk assessment and FR/EN/RU UI. The production EXE and hardware probes are not run in CI. Driver installers are not run.
