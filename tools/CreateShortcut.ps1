<#
    J6: create a desktop shortcut to the published app.

    The app is published as a folder (no installer, no machine-wide writes),
    so the shortcut is the only thing a user needs. It points at the
    published executable, with the publish folder as the working directory
    so the app finds its own files.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\CreateShortcut.ps1
        powershell -ExecutionPolicy Bypass -File tools\CreateShortcut.ps1 -PublishDir <dir> -Name <name>
#>
param(
    [string]$PublishDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\publish"),
    [string]$Name = "Renewable Energy (MTSKPY Inspection)"
)

$exe = Join-Path $PublishDir "MSKPY-Windows.exe"
if (-not (Test-Path $exe)) {
    Write-Error "The published app is not there: $exe - publish it first (dotnet publish src\Presentation -c Release -p:PublishProfile=FolderProfile)."
    exit 1
}

$desktop = [Environment]::GetFolderPath("Desktop")
$link = Join-Path $desktop "$Name.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $PublishDir
$shortcut.Description = "MTSKPY linestaff inspection - published build"
$shortcut.IconLocation = "$exe,0"
$shortcut.Save()

if (Test-Path $link) {
    Write-Output "shortcut: $link"
    Write-Output "target  : $exe"
} else {
    Write-Error "The shortcut was not created: $link"
    exit 1
}
