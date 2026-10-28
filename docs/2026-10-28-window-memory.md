# DevicePulse window controls and RAM generation

> **For agentic workers:** Execute inline with the executing-plans skill.

**Goal:** Deliver a compiled Windows x64 administrator build with maximize/restore, refined typography/spacing, and per-module DDR generation.

**Architecture:** Retain WinForms, custom caption and four-card dashboard. Read memory type from the existing SMBIOS type 17 parser, byte 0x12. Use native FormWindowState and working-area bounds.

**Tech Stack:** C#, .NET 10, WinForms, Windows Actions runner.

## Constraints
- Preserve requireAdministrator and all existing sensor sources.
- No inferred DDR type from speed. Unknown/unsupported firmware values remain unknown.
- Support French, English and Russian captions and layout.
- Maximize retains taskbar; restore returns to ordinary window bounds.

## Review focus
- Unknown firmware bytes must not imply DDR generation.
- Mixed module fixtures must retain the selected module's own type.
- Borderless maximize must use the monitor working area.
- Caption and selectors must fit compact and large windows.
- CI must never launch the final elevated binary or activate drivers.

## Tasks
- [ ] Add regression tests for DDR3/4/5, LPDDR variants, unknown data, maximize/restore/localization and 9 synthetic layouts. Run on original source and confirm expected missing-feature failures.
- [ ] Chrome.cs: add maximize/restore button and localized tooltip/icon; enable system maximize and title double click; bound maximize to active working area. MainForm.cs: enable maximize, set consistent version and spacing.
- [ ] PerformanceSensors.cs: append optional MemoryType to record and decode the firmware byte; retain existing constructor compatibility.
- [ ] PerformanceDashboard.cs: show generation in RAM card and module selector/help; apply consistent semibold headings, labels, row spacing and bounded scaling. Refine battery panel padding and caption controls.
- [ ] Run complete built-in tests plus regression suite on Windows with non-elevated QA build; inspect representative generated screenshots. Publish final administrator EXE and inspect embedded manifest.
- [ ] Package runtime, verified libcdi, notices and usage instructions. Download and verify hashes; save final ZIP and provide durable link.
