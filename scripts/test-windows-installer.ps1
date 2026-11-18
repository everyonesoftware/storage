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
function Get-ConsentBrowserText($Dialog) {
    $Dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }
}
function Get-ConsentWindowFacts($Window) {
    [StorageSmoke.Native]::ReadConsentWindow([IntPtr]::new($Window.Current.NativeWindowHandle))
}
function Assert-OwnedConsentTarget($Dialog, $Yes, $No, [int]$ServiceProcessId) {
    $owned=@(Get-OwnedProcesses | Where-Object { $_.ProcessId -eq $ServiceProcessId -and $_.ExecutablePath -ieq $serviceExe })
    if ($owned.Count -ne 1) { throw 'Consent service PID/path no longer belongs to this installation' }
    $title='Storage Service — Browser approval'
    if ($Dialog.Current.ProcessId -ne $ServiceProcessId -or $Dialog.Current.Name -cne $title) { throw 'Unexpected consent dialog owner/title' }
    if (-not @(Get-ConsentBrowserText $Dialog | Where-Object { $_.Contains($appExe) }).Count) { throw 'Service consent is not for this exact installed Browser' }
    $dialogWindow=Get-ConsentWindowFacts $Dialog
    if (-not $Dialog.Current.NativeWindowHandle -or -not $dialogWindow.Exists -or $dialogWindow.Handle -ne $Dialog.Current.NativeWindowHandle -or $dialogWindow.ProcessId -ne $ServiceProcessId -or $dialogWindow.ClassName -cne '#32770' -or $dialogWindow.Title -cne $title -or -not $dialogWindow.Enabled -or -not $dialogWindow.Visible) { throw 'Invalid native service consent dialog' }
    foreach ($entry in @(@{button=$Yes;id=6},@{button=$No;id=7})) {
        $button=$entry.button; $id=$entry.id
        if (-not $button -or $button.Current.ProcessId -ne $ServiceProcessId -or $button.Current.AutomationId -cne [string]$id -or $button.Current.ControlType.ProgrammaticName -cne 'ControlType.Button' -or -not $button.Current.IsEnabled -or -not $button.Current.NativeWindowHandle) { throw 'Invalid service-owned Yes/No UIA button' }
        $native=Get-ConsentWindowFacts $button
        if (-not $native.Exists -or $native.Handle -ne $button.Current.NativeWindowHandle -or $native.ProcessId -ne $ServiceProcessId -or $native.Parent -ne $dialogWindow.Handle -or $native.ClassName -cne 'Button' -or $native.ControlId -ne $id -or -not $native.Enabled -or -not $native.Visible) { throw 'Invalid native service-owned Yes/No button' }
    }
}
function Get-LegacyConsentPattern($Button) {
    # LegacyIAccessible is a native UIA pattern, not a managed InvokePattern.
    [StorageSmoke.LegacyAction]::FromHandle([IntPtr]::new($Button.Current.NativeWindowHandle))
}
function Invoke-OwnedConsentButton($Dialog, $Yes, $No, [int]$ServiceProcessId) {
    Assert-OwnedConsentTarget $Dialog $Yes $No $ServiceProcessId
    $pattern=$null
    # https://learn.microsoft.com/dotnet/api/system.windows.automation.automationelement.trygetcurrentpattern
    if ($Yes.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) {
        Assert-OwnedConsentTarget $Dialog $Yes $No $ServiceProcessId
        $pattern.Invoke()
        return 'InvokePattern'
    }
    # Only unsupported capability falls back. Stale elements, provider failures
    # and action errors propagate: never risk a second activation after a failure.
    $legacy=Get-LegacyConsentPattern $Yes
    if ($null -eq $legacy) { throw 'No supported consent button action (InvokePattern/LegacyIAccessible)' }
    try {
        Assert-OwnedConsentTarget $Dialog $Yes $No $ServiceProcessId
        $legacy.DoDefaultAction()
        return 'LegacyIAccessible'
    } finally { $legacy.Dispose() }
}
function Approve-OwnedService {
    foreach ($owned in @(Get-OwnedProcesses | Where-Object { $_.ExecutablePath -ieq $serviceExe })) {
        foreach ($dialog in @(Get-Elements ([int]$owned.ProcessId) 'Storage Service — Browser approval')) {
            $yesControls=@($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'6')))
            $noControls=@($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'7')))
            if ($yesControls.Count -ne 1 -or $noControls.Count -ne 1) { throw 'Expected unique service-owned Yes/No consent controls' }
            $yes=$yesControls[0]; $no=$noControls[0]
            Assert-OwnedConsentTarget $dialog $yes $no ([int]$owned.ProcessId)
            Write-Evidence @{ servicePid=$owned.ProcessId; servicePath=$owned.ExecutablePath; browser=$appExe; dialog=$dialog.Current.Name } 'consent.json'
            $dialogHandle=$dialog.Current.NativeWindowHandle
            $action=Invoke-OwnedConsentButton $dialog $yes $no ([int]$owned.ProcessId)
            Write-Evidence @{ action=$action; servicePid=$owned.ProcessId; dialogHwnd=$dialogHandle } 'consent-action.json'
            $consentDeadline=[DateTime]::UtcNow.AddSeconds(5)
            while (@(Get-Elements ([int]$owned.ProcessId) 'Storage Service — Browser approval').Count) {
                if ([DateTime]::UtcNow -ge $consentDeadline) { throw 'Consent dialog did not close after real button activation' }
                Start-Sleep -Milliseconds 100
            }
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
 // Native UIA COM prefixes in Windows SDK UIAutomationClient.h vtable order.
 // Only ElementFromHandle, GetCurrentPattern and DoDefaultAction are called.
 // https://learn.microsoft.com/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationlegacyiaccessiblepattern
 [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 internal interface IUIAutomation {
  void CompareElements(IntPtr a,IntPtr b,out int same);
  void CompareRuntimeIds(IntPtr a,IntPtr b,out int same);
  void GetRootElement(out IntPtr root);
  void ElementFromHandle(IntPtr hwnd,out IUIAutomationElement element);
 }
 [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 internal interface IUIAutomationElement {
  void SetFocus();
  void GetRuntimeId(out IntPtr id);
  void FindFirst(int scope,IntPtr condition,out IntPtr found);
  void FindAll(int scope,IntPtr condition,out IntPtr found);
  void FindFirstBuildCache(int scope,IntPtr condition,IntPtr cache,out IntPtr found);
  void FindAllBuildCache(int scope,IntPtr condition,IntPtr cache,out IntPtr found);
  void BuildUpdatedCache(IntPtr cache,out IntPtr element);
  void GetCurrentPropertyValue(int id,IntPtr value);
  void GetCurrentPropertyValueEx(int id,int ignoreDefault,IntPtr value);
  void GetCachedPropertyValue(int id,IntPtr value);
  void GetCachedPropertyValueEx(int id,int ignoreDefault,IntPtr value);
  void GetCurrentPatternAs(int id,ref Guid iid,out IntPtr pattern);
  void GetCachedPatternAs(int id,ref Guid iid,out IntPtr pattern);
  [PreserveSig] int GetCurrentPattern(int id,[MarshalAs(UnmanagedType.IUnknown)] out object pattern);
 }
 [ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 internal interface IUIAutomationLegacyIAccessiblePattern {
  void Select(int flags);
  void DoDefaultAction();
 }
 public sealed class LegacyAction : IDisposable {
  private IUIAutomationLegacyIAccessiblePattern pattern;
  private LegacyAction(IUIAutomationLegacyIAccessiblePattern value) { pattern=value; }
  public static LegacyAction FromHandle(IntPtr hwnd) {
   if(hwnd==IntPtr.Zero) throw new InvalidOperationException("Missing consent button HWND");
   IUIAutomation automation=null; IUIAutomationElement element=null; object value=null;
   try {
    automation=(IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"),true));
    automation.ElementFromHandle(hwnd,out element);
    // UIA_LegacyIAccessiblePatternId; no action if capability is absent.
    // https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationelement-getcurrentpattern
    int hr=element.GetCurrentPattern(10018,out value);
    if(hr==unchecked((int)0x80040204)) return null; // UIA_E_NOTSUPPORTED
    Marshal.ThrowExceptionForHR(hr);
    if(value==null) return null;
    var action=new LegacyAction((IUIAutomationLegacyIAccessiblePattern)value);
    value=null; // wrapper owns this RCW until Dispose
    return action;
   } finally {
    if(value!=null) Marshal.ReleaseComObject(value);
    if(element!=null) Marshal.ReleaseComObject(element);
    if(automation!=null) Marshal.ReleaseComObject(automation);
   }
  }
  public void DoDefaultAction() {
   if(pattern==null) throw new ObjectDisposedException(nameof(LegacyAction));
   pattern.DoDefaultAction();
  }
  public void Dispose() { if(pattern!=null) { Marshal.ReleaseComObject(pattern); pattern=null; } }
 }
 public sealed class ConsentWindow {
  public long Handle,Parent;
  public uint ProcessId;
  public int ControlId;
  public bool Exists,Enabled,Visible;
  public string ClassName,Title;
 }
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
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h,System.Text.StringBuilder text,int count);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,System.Text.StringBuilder text,int count);
  public static ConsentWindow ReadConsentWindow(IntPtr h) {
   uint pid; GetWindowThreadProcessId(h,out pid);
   var cls=new System.Text.StringBuilder(256); var title=new System.Text.StringBuilder(512);
   GetClassName(h,cls,cls.Capacity); GetWindowText(h,title,title.Capacity);
   return new ConsentWindow {Handle=h.ToInt64(),Parent=GetParent(h).ToInt64(),ProcessId=pid,ControlId=GetDlgCtrlID(h),Exists=IsWindow(h),Enabled=IsWindowEnabled(h),Visible=IsWindowVisible(h),ClassName=cls.ToString(),Title=title.ToString()};
  }
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
