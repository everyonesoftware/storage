import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, resolve } from 'node:path';
const root = resolve(import.meta.dirname, '..');
const read = p => readFileSync(join(root, p), 'utf8').replaceAll('\r\n','\n');
const pwsh = process.env.PWSH ?? (existsSync('/opt/data/tools/powershell-7.5.4/pwsh') ? '/opt/data/tools/powershell-7.5.4/pwsh' : 'pwsh');
const ps = code => {
 const result=spawnSync(pwsh, ['-NoProfile','-NonInteractive','-Command',code], {cwd:root,encoding:'utf8',env:{...process.env,DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:'1'},timeout:30000});
 if(result.error) throw new Error(`PowerShell 7.2+ is required for real parser/archive checks; set PWSH to its executable: ${result.error.message}`);
 return result;
};
test('native Windows packaging exists and preserves exact Storage Velopack contract', () => {
 assert.ok(existsSync(join(root,'scripts/build-windows-installer.ps1')), 'native Windows packager missing');
 const source = read('scripts/build-windows-installer.ps1');
 for (const token of ['SourceCommit','NativeDll','AllowUnsigned','StorageWindows','Storage.Browser.exe','--framework','vcredist143-x64','--skip-updates','Assert-PackagedPayload','ZipArchive','resources.pri','Microsoft.UI.Xaml.dll','Service/storage_core.dll']) assert.ok(source.includes(token), token);
 assert.equal(JSON.parse(read('apps/windows/.config/dotnet-tools.json')).tools['vpk'].version,'1.2.161');
 const app = read('apps/windows/Storage.Browser/Program.cs');
 assert.ok(app.indexOf('SetAutoApplyOnStartup(false)') < app.indexOf('InitializeComWrappers'));
 assert.match(app,/OnBeforeUninstall/);
});
test('PowerShell scripts parse, and non-Windows execution fails closed', () => {
 for (const script of ['build-windows-installer.ps1','test-windows-installer.ps1']) {
  const result = ps(`$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseFile('${join(root,'scripts',script).replaceAll("'","''")}',[ref]$tokens,[ref]$errors) | Out-Null; if($errors.Count){$errors | Out-String | Write-Error; exit 1}`);
  assert.equal(result.status,0,result.stdout+result.stderr);
 }
 if(process.platform==='win32') return; // Never execute the mutating native gate inside portable tooling tests.
 const result = ps(`& ./scripts/build-windows-installer.ps1 -Version 0.1.1 -SourceCommit '${'a'.repeat(40)}' -AllowUnsigned`);
 assert.notEqual(result.status,0);
 assert.match(result.stderr,/requires native Windows/);
 const smoke = ps('& ./scripts/test-windows-installer.ps1 -InstallerPath missing.exe -EvidenceDirectory missing');
 assert.notEqual(smoke.status,0); assert.match(smoke.stderr,/requires native Windows/);
});
test('recursive archive gate preserves nested bytes and permits only pinned Velopack additions', () => {
 const result = ps(`
 $ErrorActionPreference='Stop'
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/build-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 foreach($name in @('Get-PayloadInventory','Assert-PackagedPayload')) {
  $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
  Invoke-Expression $function.Extent.Text
 }
 $scratch=Join-Path ([IO.Path]::GetTempPath()) ('storage-archive-test-'+[guid]::NewGuid().ToString('N'))
 $publish=Join-Path $scratch 'publish'; $null=New-Item -ItemType Directory -Path (Join-Path $publish 'Service') -Force
 try {
  [IO.File]::WriteAllText((Join-Path $publish 'Storage.Browser.dll'),'browser fixture')
  [IO.File]::WriteAllText((Join-Path $publish 'Service/storage_core.dll'),'native fixture')
  function Make-Package([string]$Mode) {
   $path=Join-Path $scratch ($Mode+'.zip'); $stream=[IO.File]::Create($path)
   $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
   try {
    $entries=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $entries['lib/app/Storage.Browser.dll']='browser fixture';$entries['lib/app/Service/storage_core.dll']='native fixture'
    $entries['lib/app/sq.version']='metadata';$entries['lib/app/Squirrel.exe']='updater';$entries['lib/app/Storage_ExecutionStub.exe']='stub'
    if($Mode -eq 'corrupt'){$entries['lib/app/Service/storage_core.dll']='changed'}
    if($Mode -eq 'missing'){$null=$entries.Remove('lib/app/Service/storage_core.dll')}
    if($Mode -eq 'unexpected'){$entries['lib/app/Other.exe']='unknown'}
    if($Mode -eq 'traversal'){$entries['lib/app/../escape']='escape'}
    if($Mode -eq 'case-duplicate'){$entries['lib/app/storage.browser.DLL']='browser fixture'}
    foreach($name in $entries.Keys){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write($entries[$name]);$writer.Dispose()}
   } finally {$zip.Dispose();$stream.Dispose()}
   return $path
  }
  Assert-PackagedPayload $publish (Make-Package 'valid')
  foreach($mode in @('corrupt','missing','unexpected','traversal','case-duplicate')) {
   $rejected=$false; try {Assert-PackagedPayload $publish (Make-Package $mode)} catch {$rejected=$true}
   if(-not $rejected){throw ('Accepted '+$mode)}
  }
  Write-Output 'RECURSIVE_ARCHIVE_CHECKS_PASSED'
 } finally {Remove-Item -LiteralPath $scratch -Recurse -Force}
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/RECURSIVE_ARCHIVE_CHECKS_PASSED/);
});

test('service shutdown excludes its own CLI process and other desktop sessions', () => {
 const source=read('apps/windows/Storage.Client/ServiceLifetime.cs');
 assert.match(source,/process\.Id == Environment\.ProcessId/);
 assert.match(source,/process\.SessionId !=/);
});
test('native gate requires rendered UI, own service consent, real IPC, graceful stop and independent uninstall', () => {
 const source = read('scripts/test-windows-installer.ps1');
 for (const token of ['--installto','CopyFromScreen','UIAutomationClient','Storage Service — Browser approval','--service-health','--stop','uninstallVerified','Get-RemainingInstallation','cleanupErrors']) assert.ok(source.includes(token),token);
 assert.ok(!source.includes('--dev-browser'));
 assert.ok(!source.includes('MUSIC_'));
});

test('native uninstall exercises the hook with the approved service still running', () => {
 const source = read('scripts/test-windows-installer.ps1');
 assert.doesNotMatch(source, /'stop-before-uninstall'/);
 assert.ok(source.indexOf('$summary.uninstallStartedWithRunningService=$true') < source.indexOf("'uninstall' 40"));
 assert.match(source, /Expected one healthy task-owned service before uninstall/);
});

test('consent capability routing uses the real helper and fails closed on provider/action errors', () => {
 const result=ps(`
 $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-OwnedConsentButton'},$true)
 if(-not $function){throw 'Missing capability-aware consent activation helper'}
 Invoke-Expression $function.Extent.Text
 # Mock only unavailable OS/UI capabilities; execute the actual routing helper.
 Add-Type 'namespace Windows.Automation { public class InvokePattern { public static object Pattern = new object(); } }'
 $script:events=[Collections.Generic.List[string]]::new()
 function Assert-OwnedConsentTarget { $script:events.Add('validate'); if($script:invalid){throw 'invalid target'} }
 function Get-LegacyConsentPattern { $script:events.Add('legacy-query'); if($script:legacyQueryError){throw 'legacy query failed'}; return $script:legacy }
 function Invoke-NativeConsentButton { $script:events.Add('native-action'); if($script:actionError){throw 'action failed'} }
 $pattern=[pscustomobject]@{}
 $pattern | Add-Member ScriptMethod Invoke { $script:events.Add('invoke'); if($script:actionError){throw 'action failed'} }
 $legacyPattern=[pscustomobject]@{}
 $legacyPattern | Add-Member ScriptMethod Dispose { }
 $legacyPattern | Add-Member ScriptMethod DoDefaultAction { $script:events.Add('legacy-action'); if($script:actionError){throw 'action failed'} }
 $yes=[pscustomobject]@{Current=[pscustomobject]@{NativeWindowHandle=123}}
 $yes | Add-Member ScriptMethod TryGetCurrentPattern { param($id,$output); $script:events.Add('invoke-query'); if($script:queryError){throw 'provider failed'}; $output.Value=$script:invoke; return $null -ne $script:invoke }
 function Run-Case($expected,$message='') {
  $script:events.Clear(); $caught=''; $route=''
  try {$route=Invoke-OwnedConsentButton 'dialog' $yes 'no' 42} catch {$caught=$_.ToString()}
  if($message) {if($caught -notlike ('*'+$message+'*')){throw ('Wrong failure: '+$caught)}} elseif($caught -or $route -ne $expected){throw ('Wrong route/failure: '+$route+' '+$caught)}
  return ($script:events -join ',')
 }
 $script:invalid=$false; $script:queryError=$false; $script:legacyQueryError=$false; $script:actionError=$false
 $script:invoke=$pattern; $script:legacy=$legacyPattern
 if((Run-Case 'InvokePattern') -ne 'validate,invoke-query,validate,invoke'){throw 'Invoke priority/revalidation'}
 $script:invoke=$null
 if((Run-Case 'LegacyIAccessible') -ne 'validate,invoke-query,legacy-query,validate,legacy-action'){throw 'Unsupported Invoke must use Legacy default action'}
 $script:legacy=$null
 if((Run-Case 'NativeButtonClick') -ne 'validate,invoke-query,legacy-query,native-action'){throw 'Absent UIA patterns must use the validated native Button route'}
 $script:invalid=$true
 if((Run-Case '' 'invalid target') -ne 'validate'){throw 'Invalid target queried/activated'}
 $script:invalid=$false; $script:queryError=$true
 if((Run-Case '' 'provider failed') -ne 'validate,invoke-query'){throw 'Provider errors must not fall back'}
 $script:queryError=$false; $script:legacyQueryError=$true
 if((Run-Case '' 'legacy query failed') -ne 'validate,invoke-query,legacy-query'){throw 'Legacy errors must fail closed'}
 $script:legacyQueryError=$false; $script:invoke=$pattern; $script:actionError=$true
 if((Run-Case '' 'action failed') -ne 'validate,invoke-query,validate,invoke'){throw 'Failed Invoke must not cause duplicate activation'}
 $script:invoke=$null; $script:legacy=$legacyPattern
 if((Run-Case '' 'action failed') -ne 'validate,invoke-query,legacy-query,validate,legacy-action'){throw 'Failed Legacy must fail closed'}
 $script:legacy=$null
 if((Run-Case '' 'action failed') -ne 'validate,invoke-query,legacy-query,native-action'){throw 'Failed native action must not retry'}
 Write-Output 'CONSENT_ROUTING_PASSED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/CONSENT_ROUTING_PASSED/);
});

test('native consent activation requires foreground, responsiveness and fresh ownership', () => {
 const result=ps(`
 $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-NativeConsentButton'},$true)
 if(-not $function){throw 'Missing native consent helper'}
 Invoke-Expression $function.Extent.Text
 Add-Type @'
 using System;
 namespace StorageSmoke {
  public static class Native {
   public static bool Ready=true, AllowForeground=true, FailClick=false;
   public static IntPtr Foreground=IntPtr.Zero, Clicked=IntPtr.Zero;
   public static int Clicks;
   public static bool Responsive(IntPtr h) { return Ready; }
   public static bool SetForegroundWindow(IntPtr h) { if(AllowForeground) Foreground=h; return AllowForeground; }
   public static IntPtr GetForegroundWindow() { return Foreground; }
   public static void ClickConsentButton(IntPtr dialog,IntPtr button) { Clicks++; Clicked=button; if(FailClick) throw new InvalidOperationException("native action failed"); }
  }
 }
'@
 $dialog=[pscustomobject]@{Current=[pscustomobject]@{NativeWindowHandle=100}}
 $yes=[pscustomobject]@{Current=[pscustomobject]@{NativeWindowHandle=101}}
 $script:validations=0; $script:rejectAt=0
 function Assert-OwnedConsentTarget { $script:validations++; if($script:validations -eq $script:rejectAt){throw 'invalid ownership'} }
 function Run-Case($expected='') {
  $script:validations=0; [StorageSmoke.Native]::Clicks=0; [StorageSmoke.Native]::Foreground=[IntPtr]::Zero
  $caught=''; try {Invoke-NativeConsentButton $dialog $yes 'no' 42} catch {$caught=$_.ToString()}
  if($expected){if($caught -notlike ('*'+$expected+'*')){throw ('Wrong rejection: '+$caught)}} elseif($caught){throw $caught}
 }
 Run-Case
 if([StorageSmoke.Native]::Clicks -ne 1 -or [StorageSmoke.Native]::Clicked -ne 101 -or $script:validations -lt 2){throw 'Expected one exact button activation after revalidation'}
 $script:rejectAt=1; Run-Case 'invalid ownership'
 if([StorageSmoke.Native]::Clicks){throw 'Invalid owner activated'}
 $script:rejectAt=2; Run-Case 'invalid ownership'
 if([StorageSmoke.Native]::Clicks){throw 'Stale owner activated'}
 $script:rejectAt=0; [StorageSmoke.Native]::Ready=$false; Run-Case 'hung'
 if([StorageSmoke.Native]::Clicks){throw 'Hung dialog activated'}
 [StorageSmoke.Native]::Ready=$true; [StorageSmoke.Native]::AllowForeground=$false; Run-Case 'foreground'
 if([StorageSmoke.Native]::Clicks){throw 'Inactive dialog activated'}
 [StorageSmoke.Native]::AllowForeground=$true; [StorageSmoke.Native]::FailClick=$true; Run-Case 'native action failed'
 if([StorageSmoke.Native]::Clicks -ne 1){throw 'Native action retried after failure'}
 Write-Output 'NATIVE_CONSENT_GUARDS_PASSED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/NATIVE_CONSENT_GUARDS_PASSED/);
});

test('consent ownership accepts native Buttons reported as UIA Panes but rejects unsafe targets', () => {
 const result=ps(`
 $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-OwnedConsentTarget'},$true)
 if(-not $function){throw 'Missing ownership validation helper'}
 Invoke-Expression $function.Extent.Text
 $serviceExe='C:/isolated/current/Service/Storage.Service.exe'; $appExe='C:/isolated/current/Storage.Browser.exe'
 function Get-OwnedProcesses { [pscustomobject]@{ProcessId=42;ExecutablePath=$script:actualPath} }
 function Get-ConsentBrowserText { $script:browserText }
 function Get-ConsentWindowFacts($Window) { $script:facts[$Window.Current.NativeWindowHandle] }
 $dialog=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=42;Name='Storage Service — Browser approval';NativeWindowHandle=100}}
 $yes=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=42;AutomationId='6';IsEnabled=$true;ControlType=[pscustomobject]@{ProgrammaticName='ControlType.Button'};NativeWindowHandle=101}}
 $no=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=42;AutomationId='7';IsEnabled=$true;ControlType=[pscustomobject]@{ProgrammaticName='ControlType.Button'};NativeWindowHandle=102}}
 $script:actualPath=$serviceExe; $script:browserText='Approve '+$appExe+'?'
 $script:facts=@{
  100=@{Handle=100;Exists=$true;ProcessId=42;Parent=0;ClassName='#32770';ControlId=0;Enabled=$true;Visible=$true;Title='Storage Service — Browser approval'}
  101=@{Handle=101;Exists=$true;ProcessId=42;Parent=100;ClassName='Button';ControlId=6;Enabled=$true;Visible=$true;Title='Yes'}
  102=@{Handle=102;Exists=$true;ProcessId=42;Parent=100;ClassName='Button';ControlId=7;Enabled=$true;Visible=$true;Title='No'}
 }
 Assert-OwnedConsentTarget $dialog $yes $no 42
 # Captured hosted Windows representation: UIA Pane, native Button, matching HWND/PID/parent/ID.
 $yes.Current.ControlType.ProgrammaticName='ControlType.Pane'
 $no.Current.ControlType.ProgrammaticName='ControlType.Pane'
 Assert-OwnedConsentTarget $dialog $yes $no 42
 function Must-Reject([scriptblock]$change,[scriptblock]$restore) {
  & $change; $rejected=$false
  try {Assert-OwnedConsentTarget $dialog $yes $no 42} catch {$rejected=$true} finally {& $restore}
  if(-not $rejected){throw ('Accepted unsafe target: '+$change)}
 }
 Must-Reject {$script:actualPath='C:/other/Storage.Service.exe'} {$script:actualPath=$serviceExe}
 Must-Reject {$script:browserText='C:/other/Storage.Browser.exe'} {$script:browserText=$appExe}
 Must-Reject {$dialog.Current.ProcessId=99} {$dialog.Current.ProcessId=42}
 Must-Reject {$dialog.Current.Name='Other approval'} {$dialog.Current.Name='Storage Service — Browser approval'}
 foreach($button in @($yes,$no)) {
  Must-Reject {$button.Current.ProcessId=99} {$button.Current.ProcessId=42}
  $id=$button.Current.AutomationId
  Must-Reject {$button.Current.AutomationId='8'} {$button.Current.AutomationId=$id}
  Must-Reject {$button.Current.IsEnabled=$false} {$button.Current.IsEnabled=$true}
  $controlType=$button.Current.ControlType.ProgrammaticName
  Must-Reject {$button.Current.ControlType.ProgrammaticName='ControlType.Text'} {$button.Current.ControlType.ProgrammaticName=$controlType}
  Must-Reject {$button.Current.NativeWindowHandle=0} {$button.Current.NativeWindowHandle=100+[int]$id-5}
 }
 foreach($handle in @(100,101,102)) {
  $fact=$script:facts[$handle]
  foreach($key in @('Exists','Enabled','Visible')) {Must-Reject {$fact[$key]=$false} {$fact[$key]=$true}}
  Must-Reject {$fact.ProcessId=99} {$fact.ProcessId=42}
  $class=$fact.ClassName
  Must-Reject {$fact.ClassName='NotAButtonOrDialog'} {$fact.ClassName=$class}
  Must-Reject {$fact.Handle=999} {$fact.Handle=$handle}
  if($handle -ne 100) {
   $controlId=$fact.ControlId
   Must-Reject {$fact.ControlId=8} {$fact.ControlId=$controlId}
   Must-Reject {$fact.Parent=999} {$fact.Parent=100}
  }
 }
 Must-Reject {$script:facts[100].Title='Different dialog'} {$script:facts[100].Title='Storage Service — Browser approval'}
 Write-Output 'CONSENT_OWNERSHIP_PASSED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/CONSENT_OWNERSHIP_PASSED/);
});

test('consent rejection retains target properties before ownership validation fails', () => {
 const result=ps(`
 $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Approve-OwnedService'},$true)
 Invoke-Expression $function.Extent.Text
 Add-Type 'namespace Windows.Automation { public enum TreeScope { Descendants } public class AutomationElement { public static object AutomationIdProperty=new object(); } public class PropertyCondition { public string Id; public PropertyCondition(object p,string id) {Id=id;} } }'
 $serviceExe='isolated/service.exe'; $appExe='isolated/browser.exe'; $script:evidence=@{}
 $dialog=[pscustomobject]@{}
 $dialog | Add-Member ScriptMethod FindAll {param($scope,$condition); return $condition.Id}
 function Get-OwnedProcesses { [pscustomobject]@{ProcessId=42;ExecutablePath=$serviceExe} }
 function Get-Elements { $dialog }
 function Get-ConsentElementEvidence($Element) { @{observed=$Element} }
 function Write-Evidence($Value,$Name) { $script:evidence[$Name]=$Value }
 function Assert-OwnedConsentTarget { throw 'Invalid service-owned Yes/No UIA button' }
 function Invoke-OwnedConsentButton { throw 'Invalid target must not activate' }
 $caught=''; try {Approve-OwnedService} catch {$caught=$_.ToString()}
 if($caught -notlike '*Invalid service-owned*'){throw ('Wrong failure: '+$caught)}
 if(-not $script:evidence.ContainsKey('consent-targets.json')){throw 'Missing target evidence before rejection'}
 $snapshot=$script:evidence['consent-targets.json']
 if($snapshot.servicePid -ne 42 -or $snapshot.yes.observed -ne '6' -or $snapshot.no.observed -ne '7'){throw 'Incorrect pre-validation evidence'}
 Write-Output 'CONSENT_REJECTION_EVIDENCE_PASSED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/CONSENT_REJECTION_EVIDENCE_PASSED/);
});

test('consent completion does not read a destroyed dialog after the button closes it', () => {
 const result=ps(`
 $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
 $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $function=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Approve-OwnedService'},$true)
 Invoke-Expression $function.Extent.Text
 Add-Type 'namespace Windows.Automation { public enum TreeScope { Descendants } public class AutomationElement { public static object AutomationIdProperty=new object(); } public class PropertyCondition { public string Id; public PropertyCondition(object p,string id) {Id=id;} } }'
 $serviceExe='isolated/service.exe'; $appExe='isolated/browser.exe'; $summary=@{consentVerified=$false}
 $script:closed=$false; $script:duplicate=$false; $script:evidence=@{}
 $script:dialogCurrent=[pscustomobject]@{Name='Storage Service — Browser approval';NativeWindowHandle=100}
 $script:dialog=[pscustomobject]@{}
 $script:dialog | Add-Member ScriptProperty Current {if($script:closed){throw 'ElementNotAvailable: dialog already closed'}; $script:dialogCurrent}
 $script:dialog | Add-Member ScriptMethod FindAll {param($scope,$condition); if($script:duplicate){return @('duplicate','duplicate')}; return $condition.Id}
 function Get-OwnedProcesses { [pscustomobject]@{ProcessId=42;ExecutablePath=$serviceExe} }
 function Get-Elements { if(-not $script:closed){$script:dialog} }
 function Assert-OwnedConsentTarget { }
 function Get-ConsentElementEvidence($Element) { @{observed=$Element} }
 function Invoke-OwnedConsentButton { $script:closed=$true; 'LegacyIAccessible' }
 function Write-Evidence($Value,$Name) { $script:evidence[$Name]=$Value }
 Approve-OwnedService
 if(-not $summary.consentVerified -or $script:evidence['consent-action.json'].action -ne 'LegacyIAccessible'){throw 'Consent completion evidence missing'}
 $script:closed=$false; $script:duplicate=$true; $rejected=$false
 try {Approve-OwnedService} catch {if($_ -notlike '*unique*'){throw}; $rejected=$true}
 if(-not $rejected -or $script:closed){throw 'Duplicate controls must not activate'}
 Write-Output 'CONSENT_COMPLETION_PASSED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/CONSENT_COMPLETION_PASSED/);
});

test('native consent interop compiles and gate uses only validated real UI actions', () => {
 const source=read('scripts/test-windows-installer.ps1');
 assert.doesNotMatch(source,/\$yes\.GetCurrentPattern/);
 assert.match(source,/Invoke-OwnedConsentButton \$dialog \$yes \$no/);
 assert.match(source,/LegacyIAccessible/);
 assert.match(source,/consent-action\.json/);
 assert.match(source,/Consent dialog did not close/);
 const result=ps(`
 $ErrorActionPreference='Stop'; $tokens=$null; $errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/test-windows-installer.ps1',[ref]$tokens,[ref]$errors)
 $literal=$ast.Find({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.StartsWith('using System;')},$true)
 Add-Type -TypeDefinition $literal.Value
 if(-not ('StorageSmoke.LegacyAction' -as [type])){throw 'Native Legacy interop missing'}
 $rejected=$false
 try {[StorageSmoke.LegacyAction]::FromHandle([IntPtr]::Zero)} catch {if($_ -notlike '*Missing consent button HWND*'){throw}; $rejected=$true}
 if(-not $rejected){throw 'Zero HWND must fail before COM activation'}
 # Verify the COM ABI prefixes against the Windows SDK declaration order.
 $assembly=[StorageSmoke.LegacyAction].Assembly
 foreach($entry in @(
  @{name='IUIAutomation';guid='30cbe57d-d9d0-452a-ab13-7ac5ac4825ee';last='ElementFromHandle';count=4},
  @{name='IUIAutomationElement';guid='d22108aa-8ac5-49a5-837b-37bbb3d7591e';last='GetCurrentPattern';count=14},
  @{name='IUIAutomationLegacyIAccessiblePattern';guid='828055ad-355b-4435-86d5-3b51c14a9b1b';last='DoDefaultAction';count=2}
 )) {
  $type=$assembly.GetType('StorageSmoke.'+$entry.name)
  $methods=@($type.GetMethods() | Sort-Object MetadataToken)
  if($type.GUID.ToString() -ne $entry.guid -or $methods.Count -ne $entry.count -or $methods[-1].Name -ne $entry.last){throw ('Incorrect COM ABI prefix: '+$entry.name)}
 }
 # Compile only: never pretend that Linux provides COM, HWNDs or a Windows desktop.
 Write-Output 'CONSENT_INTEROP_COMPILED'
 `);
 assert.equal(result.status,0,result.stdout+result.stderr);
 assert.match(result.stdout,/CONSENT_INTEROP_COMPILED/);
});

test('UI Automation uses the matching PowerShell runtime assemblies before installation', () => {
 const source = read('scripts/test-windows-installer.ps1');
 assert.doesNotMatch(source, /Framework64\/v4\.0\.30319/);
 assert.match(source, /Join-Path \$PSHOME/);
 for (const name of ['UIAutomationTypes', 'UIAutomationProvider', 'UIAutomationClient', 'UIAutomationClientSideProviders']) assert.ok(source.includes(`'${name}'`));
 const preflight = source.indexOf('$null=[Windows.Automation.AutomationElement]::RootElement');
 assert.ok(preflight >= 0 && preflight < source.indexOf('$mutated=$true'));
});
