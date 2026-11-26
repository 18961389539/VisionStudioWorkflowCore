VisionStudio WPF - Internal trial 0.63.0

Verified candidate: Windows 10 Enterprise LTSC 2019 (1809), x64.
Windows 11 and Windows 10 22H2 edition-specific acceptance remain pending.
This is an internal trial, not a production-certified release.
Logical viewport tests are not actual Windows DPI acceptance.
Physical camera/PLC/robot commissioning remains unverified.
.NET 10 Windows 10 support is limited by Microsoft's LTSC/Enterprise policy:
https://learn.microsoft.com/en-us/dotnet/core/install/windows

Start:
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-VisionStudio.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-VisionStudio.ps1 -DataRoot D:\VisionStudioTrialData

Default data: %LOCALAPPDATA%\VisionStudio\Trial\data
Do not place data inside this version package. Keep the same external data root
when changing package versions. Do not delete your data or open it concurrently.
A second desktop instance using the same root is rejected. The .wpf.lock file
may remain after shutdown; its file handle, not its existence, denotes ownership.
Do not share a data root between this trial and a running Web/backend host.
Database schemas migrate forward; restoring an older application may require
restoring its matching data backup. Never assume a new database can be downgraded.

The package includes the .NET runtime, samples, replay frames and ImageViewer.
It does not require a .NET SDK, source checkout, Web server or Node.js.
Native dependencies such as the Microsoft Visual C++ 2015-2022 x64 runtime may
still be required. Vendor cameras require their own SDK and commissioning.
If startup fails, inspect the data root's logs and resolve the explicit error.

Core trial:
  1. Open an included workflow, edit node parameters, validate, save and run.
  2. Production tab: explicitly initialize the trial job, then select and start.
     Initialization does not start production or overwrite existing content.
  3. Device tab: choose a virtual/replay camera, open, grab a frame and close.
     The displayed frame is not a continuously updated video.
  4. Diagnostics tab: select a recent run, inspect details and export a ZIP.
  5. Stop production before exit. Closing waits for cancellation and cleanup.

Ctrl+S saves, Ctrl+N creates a workflow, F5 runs, Escape requests cancellation.
Unsaved changes are prompted before replacing a workflow or exiting.
Editing is disabled during execution. Failed refresh means unknown/stale status.
Confirming an alarm is not the same as resolving the underlying fault.

Limits:
  - No complete recipe creation/dataset-validation/publication management UI.
  - No physical hardware certification or unattended production guarantee.
  - Trace filtering covers the most recent 100 records.
  - Small/high-DPI viewports may require scrolling the workflow workspace.
  - Diagnostic ZIPs may contain local paths/usernames; share only as needed.
  - build-info.json declares unknown source metadata if Git is unavailable.

Existing device support remains available through explicit configuration.
By default the desktop is a trusted local Development-mode trial; it is not
a remotely authenticated production host and must not be represented as one.
Review licenses in the licenses directory before redistributing dependencies.
