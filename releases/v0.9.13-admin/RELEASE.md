# DevicePulse 0.9.13 Admin — stable binary reference

**Release label:** DevicePulse 0.9.13 Admin  
**Reference date:** 2026-10-28  
**Platform:** Windows x64, portable .NET 10 app

## Provenance

This is the stable Windows executable supplied by the project owner, not a newly compiled binary from the older sources currently in this repository. The original executable is named `DevicePulse.exe`; its **release name includes Admin**, and its embedded Windows application manifest includes:

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

Windows should therefore show a UAC elevation request when running it. This was verified by inspecting the uploaded executable's manifest bytes; the application itself was not run under Windows for this change.

## Reference checksums

| File | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `DevicePulse.exe` | 53,660,143 | `2a54bb9beecc0c024318c409cede7277c18c5e1fc465bf4b8461aa25162822ed` |
| `DevicePulse-0.9.13-PORTABLE-WIN64.zip` | 48,143,903 | `34a8f4f8aea513db67419cd38a965fdd2a4332d7266ae235596b7201fe0eb78e` |

The ZIP contains the stable executable, language instructions and a French A4 quick guide.

## Functional baseline

- Russian / French / English UI.
- Performance diagnostics for CPU, GPU, RAM, SSD/HDD and SMART where supported.
- Battery information and CPU benchmark.
- Disk power-on hours: FR **Durée de fonctionnement**, EN **Power-on hours**, RU **Общее время работы**.
- **No HTML/CSV report export** in this stable version.
- Some sensors may be unavailable when hardware or drivers do not support reading them.

## Important limitations

- This commit records **binary version identity and checksum only**, not reproducible matching 0.9.13 Admin source code.
- The `DevicePulse.exe` and portable ZIP are **not uploaded to GitHub in this commit** and are not published as GitHub Release assets here.
- A previous 0.9.8 build remains a different codebase. Do not relabel it as 0.9.13 Admin or treat it as an identical build.
- Runtime stability was designated by the user and has not been independently tested on Windows in this session.
