# MSKPY Windows — .NET 10 architecture migration

Status: current source 0.9.50. The app is a WPF linestaff-inspection client with a local SQLite store, harvested application lists, inspection editing, and the production `upload-survey` submission flow. The wire protocol is frozen from the Android app; treat live submission as a production action and use dry runs unless a live submit is explicitly approved.

Read `docs/ARCHITECTURE.md`, `docs/FEATURE-PARITY.md`, `docs/BACKEND-MAP.md`, and `docs/VALIDATION.md` before treating any feature as complete.

## Build

Use the .NET 10 SDK pinned by `global.json`. On the development laptop the permanent install is `C:\Users\vivek\dotnet10\dotnet.exe`; do not use the old Temp `opencode\dotnet10` install.

From this solution directory:

```powershell
$dotnet = "C:\Users\vivek\dotnet10\dotnet.exe"
& $dotnet build Mskpy.sln -c Release
& $dotnet run --project tests\Compatibility -c Release
& $dotnet run --project tests\Transport -c Release
& $dotnet run --project tests\ViewModels -c Release
& $dotnet run --project tests\Jsr -c Release
& $dotnet run --project tests\Exif -c Release
& $dotnet run --project tests\UiRegression -c Release -- ..\qa-net10
& $dotnet publish src\Presentation -c Release -p:PublishProfile=FolderSelfContained
robocopy artifacts\publish-portable C:\temp\mskpy-portable /MIR
```

This project preserves the original backend and previously implemented request formats. It includes the SQLite inspection store and the production submission flow; back up `%LOCALAPPDATA%\RenewableEnergyDesktop\inspection-store.db` before migrations or acceptance runs that write to the store.
