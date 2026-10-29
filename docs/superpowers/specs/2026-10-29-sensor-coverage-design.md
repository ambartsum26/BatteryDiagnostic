# DevicePulse 0.9.14 sensor compatibility

Requested outcome: improve existing disk, Intel/AMD CPU, and DDR3/4/5 identification and sensor availability. Preserve the three languages, portable Windows x64 packaging, French PawnIO installer consent, and existing interface. Do not claim universal hardware coverage.

Base: 704ef16aa34995bb8608a1e10a2fe6ee7f79c4e9 (last successful 0.9.11 source build). main records an owner-supplied 0.9.13 binary but explicitly has no matching source. This change is a separate development build, not a reconstruction of 0.9.13.

Approaches: vendor tools do not provide a single redistributable universal SDK. Existing CrystalDiskInfo/libcdi and LibreHardwareMonitor remain. Add a separate smartctl 7.5 process and its latest compatible pinned drivedb/7.5 database; retain AIDA64/HWiNFO documented shared-memory readers. This uses established model-specific decoders instead of inventing a combined vendor attribute mapping. Add Windows WMI RAM fallback with strict slot/capacity matching. Expand CPU activation only to implementations supported by LibreHardwareMonitor 0.9.6; unavailable AMD legacy families remain unavailable.

Constraints: all smartctl calls have fixed read-only options, bounded output/time, no shell, no SMART enable/self-test/firmware commands, and absolute paths. Verify packaged binaries and database before execution. Match disks by requested physical drive number plus reported model; reject ambiguous or conflicting values. Health failures override percentages; unknown values remain unknown. Keep power-on hour labels FR Durée de fonctionnement / EN Power-on hours / RU Общее время работы. No report export in this new UI.

AIDA64 is an optional external publisher, never a bundled proprietary engine. DDR generation is firmware-reported, never inferred from clock speed. Physical sensor correctness requires testing on real hardware and cannot be certified in CI.
