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

test('UI Automation uses the matching PowerShell runtime assemblies before installation', () => {
 const source = read('scripts/test-windows-installer.ps1');
 assert.doesNotMatch(source, /Framework64\/v4\.0\.30319/);
 assert.match(source, /Join-Path \$PSHOME/);
 for (const name of ['UIAutomationTypes', 'UIAutomationProvider', 'UIAutomationClient', 'UIAutomationClientSideProviders']) assert.ok(source.includes(`'${name}'`));
 const preflight = source.indexOf('$null=[Windows.Automation.AutomationElement]::RootElement');
 assert.ok(preflight >= 0 && preflight < source.indexOf('$mutated=$true'));
});
