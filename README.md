# MSKPY Windows — .NET 10 architecture migration

Status: incomplete preview 0.5. The main shell and source-derived JSR form use MVVM and four project layers. JSR includes the 19 original form fields, conditional questions, original dropdown choices, three JPEG photo roles, validation and local draft save/reopen. Full Android feature parity, the legacy inspection editor's MVVM conversion, and live inspection/JSR transport remain unfinished.

Read `docs/ARCHITECTURE.md`, `docs/FEATURE-PARITY.md`, `docs/BACKEND-MAP.md`, and `docs/VALIDATION.md` before treating any feature as complete.

## Build

Use .NET 10 SDK 10.0.403 or a compatible patch. On the development laptop it is installed locally at `work/tools/dotnet10/dotnet.exe` relative to the enclosing workspace.

From this solution directory:

```powershell
& '..\tools\dotnet10\dotnet.exe' build Mskpy.sln -c Release
& '..\tools\dotnet10\dotnet.exe' run --project tests/Compatibility -c Release
& '..\tools\dotnet10\dotnet.exe' run --project tests/Transport -c Release
& '..\tools\dotnet10\dotnet.exe' run --project tests/ViewModels -c Release
& '..\tools\dotnet10\dotnet.exe' run --project tests/Jsr -c Release
& '..\tools\dotnet10\dotnet.exe' run --project tests/UiRegression -c Release -- ..\qa-net10
```

This project preserves the original backend and previously implemented request formats. It introduces no production submission flow and no SQLite dependency. The earlier .NET 9 preview remains available separately.
