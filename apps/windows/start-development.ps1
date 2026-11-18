param([switch]$EnrollDevelopmentBrowser, [string]$Deployment = (Join-Path $PSScriptRoot "artifacts/Browser"))
$ErrorActionPreference = "Stop"
if (-not $EnrollDevelopmentBrowser) { throw "Explicit opt-in required: ./start-development.ps1 -EnrollDevelopmentBrowser. This grants this exact unpackaged Browser executable and assembly owner capabilities for the current user's local repository. It is not a malware sandbox." }
$browser = (Resolve-Path (Join-Path $Deployment "Storage.Browser.exe")).Path
$assembly = (Resolve-Path (Join-Path $Deployment "Storage.Browser.dll")).Path
$service = (Resolve-Path (Join-Path $Deployment "Service/Storage.Service.exe")).Path
$exeHash = (Get-FileHash $browser -Algorithm SHA256).Hash
$dllHash = (Get-FileHash $assembly -Algorithm SHA256).Hash
Write-Warning "DEVELOPMENT ONLY: approving this exact unpackaged Browser EXE + DLL. Stop any existing service first. No third-party app is enrolled. Other same-user full-trust software is not sandboxed."
Start-Process -FilePath $service -WorkingDirectory (Split-Path $service) -ArgumentList @("--dev-browser", "`"$browser`"", "--dev-sha256", $exeHash, "--dev-assembly-sha256", $dllHash)
Write-Host "The independent background service is starting. Click its tray icon for Start/Stop, Restart, Quit and the Open Storage header shortcut. The service remains runnable without the Browser."
Start-Process -FilePath $browser -WorkingDirectory (Split-Path $browser)
