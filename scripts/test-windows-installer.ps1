#Requires -Version 7.2
# Disposable, unlocked Windows account only. Never extracts an installer as a substitute.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath, [Parameter(Mandatory)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Installer lifecycle requires native Windows and an unlocked interactive desktop.' }
$started=[DateTime]::UtcNow
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if ((Test-Path -LiteralPath $evidence) -and @(Get-ChildItem -LiteralPath $evidence -Force).Count) { throw 'EvidenceDirectory must be new/empty' }
$null = New-Item -ItemType Directory -Path $evidence -Force
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ('StorageWindows-smoke-' + [guid]::NewGuid().ToString('N'))
$profile = Join-Path $env:LOCALAPPDATA 'Everyonesoftware/Storage'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StorageWindows'
$serviceExe = Join-Path $installRoot 'current/Service/Storage.Service.exe'
$appExe = Join-Path $installRoot 'current/Storage.Browser.exe'
$mutated = $false
$summary = [ordered]@{ status='failed'; installRoot=$installRoot; installVerified=$false; windowVerified=$false; consentVerified=$false; ipcVerified=$false; uninstallStartedWithRunningService=$false; uninstallVerified=$false; cleanupErrors=@(); failure=$null }
function Write-Evidence($Value, [string]$Name) { ConvertTo-Json -InputObject $Value -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence $Name) -Encoding utf8 }
function Invoke-Bounded([string]$Executable, [string[]]$Arguments, [string]$Label, [int]$Seconds=35) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false; $info.WorkingDirectory=$evidence
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try {
        $null=$process.Start(); $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Seconds*1000)) { $process.Kill($true); $null=$process.WaitForExit(3000); throw "$Label timeout" }
        if (-not $stdout.Wait(3000) -or -not $stderr.Wait(3000)) { throw "$Label logs did not close" }
        $stdout.Result | Set-Content (Join-Path $evidence "$Label-stdout.txt"); $stderr.Result | Set-Content (Join-Path $evidence "$Label-stderr.txt")
        if ($process.ExitCode -ne 0) { throw "$Label failed: $($process.ExitCode)" }
    } finally { if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(3000) }; $process.Dispose() }
}
function Get-OwnedProcesses {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase) }
}
function Get-StorageLinks {
    $shell=New-Object -ComObject WScript.Shell
    try {
        foreach ($directory in @([Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('DesktopDirectory'))) {
            foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.lnk' -Recurse -File) {
                $shortcut=$shell.CreateShortcut($file.FullName)
                try {
                    if ($file.BaseName -in @('Storage','StorageWindows') -or $shortcut.TargetPath.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {
                        [pscustomobject]@{ path=$file.FullName; target=$shortcut.TargetPath }
                    }
                } finally { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
            }
        }
    } finally { $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
function Get-RemainingInstallation {
    [pscustomobject]@{ files=@(if(Test-Path -LiteralPath $installRoot){Get-ChildItem -LiteralPath $installRoot -Recurse -File -Force | Select-Object -ExpandProperty FullName}); registryPresent=(Test-Path -LiteralPath $registry); shortcuts=@(Get-StorageLinks); processes=@(Get-OwnedProcesses | Select-Object ProcessId,ExecutablePath) }
}
function Get-Elements([int]$ProcessId, [string]$Title) {
    $condition=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Title))
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
}
function Approve-OwnedService {
    foreach ($owned in @(Get-OwnedProcesses | Where-Object { $_.ExecutablePath -ieq $serviceExe })) {
        foreach ($dialog in @(Get-Elements ([int]$owned.ProcessId) 'Storage Service — Browser approval')) {
            $text=$dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
            if (-not @($text | Where-Object { $_.Current.Name.Contains($appExe) }).Count) { throw 'Service consent is not for this exact installed Browser' }
            $yes=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'6'))
            $no=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'7'))
            if (-not $yes -or -not $no) { throw 'Expected service-owned Yes/No consent controls missing' }
            Write-Evidence @{ servicePid=$owned.ProcessId; servicePath=$owned.ExecutablePath; browser=$appExe; dialog=$dialog.Current.Name } 'consent.json'
            ([Windows.Automation.InvokePattern]$yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
            $summary.consentVerified=$true
        }
    }
}
function Save-WindowScreenshot($Window) {
    $handle=[IntPtr]::new($Window.Current.NativeWindowHandle)
    if (-not [StorageSmoke.Native]::Responsive($handle)) { throw 'Browser window is hung' }
    $null=[StorageSmoke.Native]::ShowWindow($handle,3); $null=[StorageSmoke.Native]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 750
    if ([StorageSmoke.Native]::GetForegroundWindow() -ne $handle) { throw 'Cannot foreground Browser for screenshot' }
    $rect=[StorageSmoke.Native+RECT]::new()
    if ([StorageSmoke.Native]::DwmGetWindowAttribute($handle,9,[ref]$rect,[Runtime.InteropServices.Marshal]::SizeOf($rect)) -ne 0) { throw 'DWM frame unavailable' }
    $bounds=[Windows.Forms.SystemInformation]::VirtualScreen
    $width=$rect.Right-$rect.Left; $height=$rect.Bottom-$rect.Top
    if ($width -lt 100 -or $height -lt 100 -or $rect.Left -lt $bounds.Left -or $rect.Top -lt $bounds.Top -or $rect.Right -gt $bounds.Right -or $rect.Bottom -gt $bounds.Bottom) { throw 'Invalid/offscreen Browser bounds' }
    $image=[Drawing.Bitmap]::new($width,$height); $graphics=[Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$image.Size)
        $colors=[Collections.Generic.HashSet[int]]::new()
        for($x=0;$x -lt $width;$x+=19){for($y=0;$y -lt $height;$y+=19){$null=$colors.Add($image.GetPixel($x,$y).ToArgb())}}
        $image.Save((Join-Path $evidence 'window.png'),[Drawing.Imaging.ImageFormat]::Png)
        if ($colors.Count -lt 8) { throw 'Blank screenshot' }
        Write-Evidence @{ width=$width; height=$height; sampledColors=$colors.Count; sha256=(Get-FileHash (Join-Path $evidence 'window.png')).Hash } 'screenshot.json'
    } finally { $graphics.Dispose(); $image.Dispose() }
    if (-not [StorageSmoke.Native]::Responsive($handle)) { throw 'Browser became unresponsive' }
}
try {
    if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) { throw 'Unlocked interactive desktop required, not assumed on CI' }
    if ((Test-Path -LiteralPath $profile) -or (Test-Path -LiteralPath $registry) -or (Test-Path (Join-Path $env:LOCALAPPDATA 'StorageWindows')) -or @(Get-StorageLinks).Count -or @(Get-Process -Name Storage.Browser,Storage.Service -ErrorAction SilentlyContinue).Count) { throw 'Existing Storage installation/profile/process: refusing to alter personal repositories or other tasks' }
    $installer=(Resolve-Path -LiteralPath $InstallerPath).Path
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    # PowerShell ships the UIA assemblies matching its own modern .NET runtime.
    # Mixing Framework 4.x assemblies with pwsh's client-side provider causes
    # UIAutomationTypes version-binding failures when RootElement is accessed.
    foreach($name in @('UIAutomationTypes','UIAutomationProvider','UIAutomationClient','UIAutomationClientSideProviders')) {
        Add-Type -Path (Join-Path $PSHOME "$name.dll")
    }
    # Resolve the actual provider before installing or launching anything.
    $null=[Windows.Automation.AutomationElement]::RootElement
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace StorageSmoke {
 public static class Native {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h,int a,out RECT r,int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h,uint m,UIntPtr w,IntPtr l,uint f,uint t,out UIntPtr r);
  [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint f,bool i,uint a);
  [DllImport("user32.dll")] public static extern bool SwitchDesktop(IntPtr d);
  [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr d);
  public static bool Responsive(IntPtr h) { UIntPtr r; return SendMessageTimeout(h,0,UIntPtr.Zero,IntPtr.Zero,2,1000,out r)!=IntPtr.Zero; }
 }
}
'@
    $null=[StorageSmoke.Native]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    $desktop=[StorageSmoke.Native]::OpenInputDesktop(0,$false,0x0100)
    if ($desktop -eq [IntPtr]::Zero) { throw 'No accessible input desktop' }
    try { if(-not [StorageSmoke.Native]::SwitchDesktop($desktop)){throw 'Locked desktop'} } finally { $null=[StorageSmoke.Native]::CloseDesktop($desktop) }
    $mutated=$true
    Invoke-Bounded $installer @('--silent','--installto',$installRoot,'--log',(Join-Path $evidence 'install.log')) 'install' 45
    foreach ($relative in @('Update.exe','current/resources.pri','current/Storage.Browser.exe','current/Microsoft.UI.Xaml.dll','current/Microsoft.WindowsAppRuntime.dll','current/coreclr.dll','current/hostfxr.dll','current/Service/Storage.Service.exe','current/Service/storage_core.dll','current/Service/coreclr.dll')) {
        if ((Get-Item -LiteralPath (Join-Path $installRoot $relative)).Length -le 0) { throw "Empty installed payload: $relative" }
    }
    $registration=Get-ItemProperty -LiteralPath $registry
    if ($registration.InstallLocation.TrimEnd('\') -ine $installRoot.TrimEnd('\')) { throw 'Registration points at a different installation' }
    $links=@(Get-StorageLinks)
    if (-not @($links | Where-Object {$_.target.StartsWith($installRoot+'\',[StringComparison]::OrdinalIgnoreCase)}).Count) { throw 'No task-install shortcut' }
    Write-Evidence $links 'shortcuts.json'; $summary.installVerified=$true
    $app=Start-Process -FilePath $appExe -WorkingDirectory $evidence -PassThru -RedirectStandardOutput (Join-Path $evidence 'browser-stdout.txt') -RedirectStandardError (Join-Path $evidence 'browser-stderr.txt')
    $deadline=[DateTime]::UtcNow.AddSeconds(65); $window=$null
    while([DateTime]::UtcNow -lt $deadline) {
        $app.Refresh(); if($app.HasExited){throw "Installed Browser exited: $($app.ExitCode)"}
        Approve-OwnedService
        foreach ($candidate in @(Get-Elements $app.Id 'Storage')) {
            # Fit the real UI to the runner display before checking realized controls.
            $null=[StorageSmoke.Native]::ShowWindow([IntPtr]::new($candidate.Current.NativeWindowHandle),3)
            $elements=$candidate.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
            $connected=@($elements | Where-Object { $_.Current.Name.Contains('Local service connected') -and -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 })
            $empty=@($elements | Where-Object { $_.Current.Name -eq 'Your space, ready for its first item' -and -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Height -gt 0 })
            if($connected.Count -and $empty.Count){$window=$candidate; break}
        }
        if($window){break}; Start-Sleep -Milliseconds 500
    }
    if(-not $window -or -not $summary.consentVerified){throw 'No realized connected Browser/empty state and owned-service consent'}
    Save-WindowScreenshot $window; $summary.windowVerified=$true
    $null=[StorageSmoke.Native]::PostMessage([IntPtr]::new($window.Current.NativeWindowHandle),0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    if(-not $app.WaitForExit(10000)){throw 'Browser did not close gracefully'}
    # The diagnostic executable is the real installed Browser, with production OS peer checks.
    Invoke-Bounded $appExe @('--service-health',(Join-Path $evidence 'health.json')) 'ipc-health' 15
    $health=Get-Content (Join-Path $evidence 'health.json') -Raw | ConvertFrom-Json
    if($health.status -ne 'healthy' -or -not $health.deviceId -or -not $health.dataStoreId){throw 'Actual service IPC health failed'}
    # Restart the independently running service without approval arguments: persisted grant must work.
    Invoke-Bounded $serviceExe @('--stop') 'stop-before-restart' 15
    $service=Start-Process -FilePath $serviceExe -WorkingDirectory $evidence -PassThru -RedirectStandardOutput (Join-Path $evidence 'service-stdout.txt') -RedirectStandardError (Join-Path $evidence 'service-stderr.txt')
    Start-Sleep -Milliseconds 750
    Invoke-Bounded $appExe @('--service-health',(Join-Path $evidence 'health-restarted.json')) 'ipc-health-restarted' 15
    $restarted=Get-Content (Join-Path $evidence 'health-restarted.json') -Raw | ConvertFrom-Json
    if($restarted.status -ne 'healthy' -or $restarted.deviceId -ne $health.deviceId -or $restarted.dataStoreId -ne $health.dataStoreId){throw 'Service restart identity/authorization failed'}
    $summary.ipcVerified=$true
    # Leave the healthy service active: uninstall must run the real Velopack
    # shutdown hook rather than pass only because this test pre-stopped it.
    $ownedServices=@(Get-OwnedProcesses | Where-Object { $_.ExecutablePath -ieq $serviceExe })
    if($ownedServices.Count -ne 1 -or $ownedServices[0].ProcessId -ne $service.Id){throw 'Expected one healthy task-owned service before uninstall'}
    $summary.uninstallStartedWithRunningService=$true
    $grant=Join-Path $profile 'service-config/bundled-browser-grant.json'; $grantHash=(Get-FileHash -LiteralPath $grant).Hash
    Invoke-Bounded (Join-Path $installRoot 'Update.exe') @('--uninstall','--silent','--log',(Join-Path $evidence 'uninstall.log')) 'uninstall' 40
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do { $remaining=Get-RemainingInstallation; if(-not $remaining.files.Count -and -not $remaining.registryPresent -and -not $remaining.shortcuts.Count -and -not $remaining.processes.Count){break}; Start-Sleep -Milliseconds 500 } while([DateTime]::UtcNow -lt $deadline)
    Write-Evidence $remaining 'uninstall-remaining.json'
    if($remaining.files.Count -or $remaining.registryPresent -or $remaining.shortcuts.Count -or $remaining.processes.Count){throw 'Uninstall left payload/registration/shortcuts/processes'}
    if((Get-FileHash -LiteralPath $grant).Hash -ne $grantHash -or -not (Test-Path (Join-Path $profile 'repository'))){throw 'Uninstall altered service grants/repository state'}
    $summary.uninstallVerified=$true; $summary.status='passed'
} catch {
    $summary.failure=$_.ToString()
    ($_ | Format-List * -Force | Out-String) | Set-Content (Join-Path $evidence 'failure.txt')
} finally {
    if($mutated) {
        # Graceful service stop first prevents native DLL locks. Forced termination is failure cleanup only.
        try { if(Test-Path -LiteralPath $serviceExe){Invoke-Bounded $serviceExe @('--stop') 'cleanup-stop' 15} } catch { $summary.cleanupErrors+="Graceful service stop: $_" }
        try {
            foreach($owned in @(Get-OwnedProcesses)) {
                $process=Get-Process -Id $owned.ProcessId -ErrorAction SilentlyContinue
                if($process -and $process.Path -ieq $owned.ExecutablePath){$summary.cleanupErrors+='Forced termination required for a task-owned process'; $process.Kill($true); if(-not $process.WaitForExit(3000)){throw 'Owned process survived cleanup'}}
            }
            if(@(Get-OwnedProcesses).Count){throw 'Orphan task-owned process'}
        } catch { $summary.cleanupErrors+="Processes: $_" }
        try {
            if($summary.status -ne 'passed' -and (Test-Path (Join-Path $installRoot 'Update.exe'))){Invoke-Bounded (Join-Path $installRoot 'Update.exe') @('--uninstall','--silent') 'cleanup-uninstall' 30}
            # Only state absent at preflight and created by this isolated test may be removed.
            if(Test-Path -LiteralPath $profile){Remove-Item -LiteralPath $profile -Recurse -Force}
            if(Test-Path -LiteralPath $profile){throw 'Task-created profile survived cleanup'}
            if(Test-Path -LiteralPath $installRoot){Remove-Item -LiteralPath $installRoot -Recurse -Force}
            if(Test-Path -LiteralPath $installRoot){throw 'Task-created install root survived cleanup'}
            $remaining=Get-RemainingInstallation
            if($remaining.registryPresent -or $remaining.shortcuts.Count -or $remaining.processes.Count){throw 'Orphans remain after cleanup'}
        } catch { $summary.cleanupErrors+="Removal: $_" }
    }
    if($summary.status -ne 'passed') {
        try {
            Write-Evidence @(Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$started.ToLocalTime() } -MaxEvents 30 -ErrorAction Stop |
                Where-Object { $_.ProviderName -in @('Application Error','.NET Runtime','Windows Error Reporting') } |
                Select-Object TimeCreated,Id,ProviderName,Message) 'application-events.json'
        } catch { $_.ToString() | Set-Content (Join-Path $evidence 'events-unavailable.txt') }
    }
    if($summary.cleanupErrors.Count){$summary.status='failed'}
    Write-Evidence $summary 'summary.json'
}
$summary | ConvertTo-Json -Depth 8
if($summary.status -ne 'passed'){exit 1}
