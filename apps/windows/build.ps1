param([string]$Dotnet = "dotnet", [string]$NativeDll = (Join-Path $PSScriptRoot "../../artifacts/native/windows-x64/storage_core.dll"))
$ErrorActionPreference = "Stop"
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq "Core") { throw "Run on native Windows; Linux compile-only output is not a runnable WinUI deployment." }
if (-not (Test-Path $NativeDll)) { throw "Build the real Windows Rust storage_core.dll first: $NativeDll" }
$out = Join-Path $PSScriptRoot "artifacts/Browser"
& $Dotnet publish (Join-Path $PSScriptRoot "Storage.Browser/Storage.Browser.csproj") -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed." }
& $Dotnet publish (Join-Path $PSScriptRoot "Storage.Service/Storage.Service.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $out "Service")
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }
Copy-Item $NativeDll (Join-Path $out "Service/storage_core.dll") -Force
foreach ($file in @("Storage.Browser.exe", "Storage.Browser.dll", "resources.pri", "Service/Storage.Service.exe", "Service/storage_core.dll")) {
    if (-not (Test-Path (Join-Path $out $file))) { throw "Incomplete deployment: $file" }
}
Write-Host "Unpackaged development deployment: $out"
Write-Host "This is not an MSIX or signed installer. Default service authorization denies unpackaged Browser. Review start-development.ps1 before explicitly opting in."
