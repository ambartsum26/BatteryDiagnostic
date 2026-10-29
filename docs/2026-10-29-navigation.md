# DevicePulse 0.9.15: navigation without hover help

User instruction: remove all tooltip windows on all screens, including buttons; keep important readouts and optional detail inside the existing UI.

Cause: PerformanceDashboard installed one tooltip over the whole painted card, another on each selector, and repeated long disk diagnostics with a 450 ms delay and 20 s duration. Battery readouts, decorative indicators, caption buttons and native SMART table cells also generated tips.

Change: remove ToolTip providers, card hover handling and indicator click/keyboard balloons. Disable native language-menu and SMART-cell hints. Retain accessibility descriptions. Battery details expand/collapse inside the existing scrollable card; SMART remains an explicit action. RU/FR/EN strings updated to remove obsolete hover directions. Preserve the clean 10-file portable package requested earlier.

Regression evidence before implementation: Windows Actions 40840349336, NavigationTests failed 21/36 checks on the prior UI, as expected. Test source compiled successfully and the old non-navigation suites passed. Updated tests cover all three languages, the new inline expansion, menu and cell behavior, and retained SMART values. Physical sensor collection is unchanged by this UI fix.
