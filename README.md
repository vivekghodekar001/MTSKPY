# MSKPY Windows — .NET 10 architecture migration

Status: current source 0.9.51. The app is a WPF linestaff-inspection client with a local SQLite store, harvested application lists, inspection editing, and the production `upload-survey` submission flow. The wire protocol is frozen from the Android app; treat live submission as a production action and use dry runs unless a live submit is explicitly approved.

Read `docs/ARCHITECTURE.md`, `docs/FEATURE-PARITY.md`, `docs/BACKEND-MAP.md`, and `docs/VALIDATION.md` before treating any feature as complete.

## Install on another PC

### Easiest: portable zip (no Git, no SDK)

1. On the main PC, build and publish the latest source (below), then zip `C:\temp\mskpy-portable` as `MSKPY-Windows-<version>.zip`.
2. On the other PC, unzip it to a folder such as `C:\temp\mskpy-portable`.
3. Run `Setup.cmd` in that folder.
4. Confirm the app title shows the expected version before using it.

GitHub holds the source code. It does not hold a ready-to-run app zip, and it does not carry the local installer list or inspection data.

### From source with GitHub

Install Git and the .NET 10 SDK first. If `git` says "not recognized", Git is not installed or PowerShell was opened before the install finished; install Git, close PowerShell, and open it again.

```powershell
winget install --id Git.Git -e --source winget
winget install --id Microsoft.DotNet.SDK.10 -e --source winget
```

If `winget` is unavailable, install Git for Windows from git-scm.com and install .NET 10 into your user folder:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:USERPROFILE\dotnet10"
```

Then clone and build:

```powershell
cd $env:USERPROFILE
git clone https://github.com/vivekghodekar001/MTSKPY.git
cd MTSKPY
git log -1 --oneline
dotnet --list-sdks
```

`git log -1 --oneline` must show the latest commit. `dotnet --list-sdks` must show a 10.0.4xx SDK. If `dotnet` is not recognized but you used the user-folder install, set `$dotnet = "$env:USERPROFILE\dotnet10\dotnet.exe"` and use `& $dotnet` in place of `dotnet` below.

```powershell
dotnet build Mskpy.sln -c Release
dotnet run --project tests\Compatibility -c Release
dotnet run --project tests\Transport -c Release
dotnet run --project tests\ViewModels -c Release
dotnet run --project tests\Jsr -c Release
dotnet run --project tests\Exif -c Release
dotnet run --project tests\UiRegression -c Release -- ..\qa-net10
dotnet publish src\Presentation -c Release -p:PublishProfile=FolderSelfContained
robocopy artifacts\publish-portable C:\temp\mskpy-portable /MIR
C:\temp\mskpy-portable\Setup.cmd
```

Run every command from inside the cloned `MTSKPY` folder. If `cd MTSKPY` says the path does not exist, the clone failed earlier; fix the Git install first and clone again. If build or any test fails, stop and fix that error before publishing or zipping.

## Installer list and local data

The installer list is universal inside the app: an installer added in any inspection appears for every customer on that PC. The list and inspection data are stored locally in `%LOCALAPPDATA%\RenewableEnergyDesktop\inspection-store.db`; GitHub does not sync them between PCs. A fresh PC starts with an empty installer list until installers are added there.

To carry existing data to another PC, close MSKPY-Windows on both PCs, back up the destination `inspection-store.db`, then copy the source `inspection-store.db` into the destination `%LOCALAPPDATA%\RenewableEnergyDesktop\` folder. Back up `%LOCALAPPDATA%\RenewableEnergyDesktop\inspection-store.db` before migrations or acceptance runs that write to the store.

This project preserves the original backend and previously implemented request formats. It includes the SQLite inspection store and the production submission flow.
