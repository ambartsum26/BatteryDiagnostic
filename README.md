# BatteryDiagnostic 0.7.9

Windows x64 portable application. Original source.zip is extracted and overrides/src files are applied before compilation. .NET 10 is bundled with the published EXE.

0.7.9 improves readability, labels reported clocks and configured memory rate, timestamps readings, clears stale values, decodes NVMe power cycles and data units, and displays endurance, spare and media errors independently of critical flags. Historical media errors are a caution, not proof of current failure.

CI runs a separate non-elevated fixture harness for parsers, assessment and UI rendering in FR/EN/RU. Fixture previews use synthetic data. The production EXE, hardware probes and driver installers are not executed in CI.
