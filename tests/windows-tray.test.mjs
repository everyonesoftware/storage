import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import '../apps/windows/Storage.Tray.Tests/native-contract.test.mjs';
const root = resolve(import.meta.dirname, '..');
const read = name => readFileSync(resolve(root, name), 'utf8').replaceAll('\r\n', '\n');
const pwsh = process.env.PWSH ?? (existsSync('/opt/data/tools/powershell-7.5.4/pwsh') ? '/opt/data/tools/powershell-7.5.4/pwsh' : 'pwsh');
function powershell(code) {
  const result = spawnSync(pwsh, ['-NoProfile', '-NonInteractive', '-Command', code], {
    cwd: root, encoding: 'utf8', timeout: 30000,
    env: { ...process.env, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1' },
  });
  if (result.error) throw result.error;
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result.stdout;
}
function pe(subsystem) {
  // Test-only PE header; never an installable application.
  const bytes = Buffer.alloc(512);
  bytes.writeUInt16LE(0x5a4d, 0);
  bytes.writeUInt32LE(128, 60);
  bytes.writeUInt32LE(0x4550, 128);
  bytes.writeUInt16LE(0x8664, 132);
  bytes.writeUInt16LE(240, 148);
  bytes.writeUInt16LE(0x20b, 152);
  bytes.writeUInt16LE(subsystem, 220);
  return bytes.toString('base64');
}
test('standalone executable gate rejects console service apphosts, malformed optional headers and non-GUI subsystems', () => {
  const output = powershell(`
    $ErrorActionPreference='Stop'
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile('./scripts/build-windows-installer.ps1',[ref]$tokens,[ref]$errors)
    $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-X64Pe'},$true)
    Invoke-Expression $fn.Extent.Text
    $scratch=Join-Path ([IO.Path]::GetTempPath()) ('storage-gui-pe-'+[guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory $scratch
    try {
      $gui=Join-Path $scratch 'gui.exe'; $console=Join-Path $scratch 'console.exe'
      [IO.File]::WriteAllBytes($gui,[Convert]::FromBase64String('${pe(2)}'))
      [IO.File]::WriteAllBytes($console,[Convert]::FromBase64String('${pe(3)}'))
      Assert-X64Pe $gui -RequireGui
      $rejected=$false; try { Assert-X64Pe $console -RequireGui } catch { $rejected=$true }
      if(-not $rejected){throw 'Console service apphost was accepted'}
      $bad=[Convert]::FromBase64String('${pe(2)}'); $bad[148]=0; $bad[149]=0
      $malformed=Join-Path $scratch 'malformed.exe'; [IO.File]::WriteAllBytes($malformed,$bad)
      $rejected=$false; try { Assert-X64Pe $malformed -RequireGui } catch { $rejected=$true }
      if(-not $rejected){throw 'Missing optional header was accepted'}
      # The core DLL still needs only the existing x64 PE gate.
      Assert-X64Pe $console
      Write-Output 'GUI_SUBSYSTEM_GATE_PASSED'
    } finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
  `);
  assert.match(output, /GUI_SUBSYSTEM_GATE_PASSED/);
});

test('Windows publish, install and development entrypoints require the tray icon and do not advertise console control', () => {
  const packager = read('scripts/build-windows-installer.ps1');
  assert.match(packager, /'Service\/Assets\/Storage\.ico'/);
  assert.match(packager, /Assert-X64Pe \(Join-Path \$Directory \$name\) -RequireGui/);
  assert.match(read('apps/windows/build.ps1'), /"Service\/Assets\/Storage\.ico"/);
  assert.match(read('scripts/test-windows-installer.ps1'), /'current\/Service\/Assets\/Storage\.ico'/);
  const development = read('apps/windows/start-development.ps1');
  assert.doesNotMatch(development, /console service|Close its console|Ctrl\+C/);
  assert.match(development, /tray/);
});

test('normal CI runs the tray regression guards', () => {
  assert.match(read('.github/workflows/ci.yml'), /node --test[^\n]+tests\/windows-tray\.test\.mjs/);
});

test('the regular Windows harness executes portable lifecycle and Browser activation behaviors', () => {
  const project = read('apps/windows/Storage.Tests/Storage.Tests.csproj');
  const entrypoint = read('apps/windows/Storage.Tests/Program.cs');
  assert.ok(project.includes('../Storage.Tray.Tests/Storage.Tray.Tests.csproj'), 'tray behavior suite must run in normal validation');
  assert.ok(project.includes('../Storage.Browser.Activation.Tests/Storage.Browser.Activation.Tests.csproj'), 'activation behavior suite must run in normal validation');
  assert.ok(entrypoint.includes('await Storage.Tray.Tests.TrayTestSuite.Run();'), 'tray suite must be executed, not merely referenced');
  assert.ok(entrypoint.includes('await Storage.Browser.Activation.Tests.ActivationTestSuite.Run();'), 'activation suite must be executed, not merely referenced');
});

test('header shortcut labels the Browse button without adding a helper row', () => {
  const native = read('apps/windows/Storage.Service/NativeTrayWindow.cs');
  assert.ok(native.includes('Label("Storage Service", 105)'), 'header must use the approved Storage Service title');
  assert.match(native, /AddButton\(101, BrowseLabel, PushButton\)/);
  assert.match(native, /BrowseLabel = "Browse"/);
  assert.match(native, /DrawText\(dc, BrowseLabel,/);
  assert.match(native, /Text = "Open Storage Browser"/);
  assert.match(native, /DrawFolder\(dc,/);
  assert.ok(!native.includes('errorLabel'), 'error details belong in the status, not an always-reserved extra row');
  assert.ok(!native.includes('"Status" :'), 'normal status must not introduce a redundant Status label');
});

test('OS session shutdown drains within its handler rather than posting an asynchronous quit', () => {
  const native = read('apps/windows/Storage.Service/NativeTrayWindow.cs');
  assert.ok(/case 0x0016: if \(wParam != 0\) EndSession\(\); return 0;/.test(native), 'WM_ENDSESSION must not return before attempting the bounded drain');
  assert.ok(native.includes('controller.EndSession(TimeSpan.FromSeconds(4))'), 'session handler must bound its service drain');
});

test('Startup draws as a switch while retaining native toggle semantics', () => {
  const tray = read('apps/windows/Storage.Service/NativeTrayWindow.cs');
  assert.match(tray, /StartupLabel = "Launch on Startup"/);
  assert.match(tray, /AddButton\(StartupId, StartupLabel, CheckBox\)/);
  assert.match(tray, /DrawStartupSwitch\(draw\.DC, draw\.Rect, ReadStartupCheck\(\),/);
  assert.match(tray, /int thumbLeft = check \? .* : /);
  assert.match(tray, /Classes = ICC_STANDARD_CLASSES \| ICC_BAR_CLASSES/);
  assert.match(read('apps/windows/Storage.Service/Storage.Service.csproj'), /<ApplicationManifest>app\.manifest<\/ApplicationManifest>/);
  assert.match(read('apps/windows/Storage.Service/app.manifest'), /name="Microsoft\.Windows\.Common-Controls"\s+version="6\.0\.0\.0"/);
});

test('Startup persists the selected state and uninstall alone clears its registration', () => {
  const tray = read('apps/windows/Storage.Service/NativeTrayWindow.cs');
  const browser = read('apps/windows/Storage.Browser/Program.cs');
  assert.match(tray, /case StartupId:\s*SetStartup\(\)/);
  assert.match(tray, /SendMessage\(buttons\[StartupId\], BM_GETCHECK, 0, 0\) == 1/);
  assert.match(tray, /bool enabled = ReadStartupCheck\(\)/);
  assert.match(tray, /WindowsStartup\.SetEnabled\(executable, enabled\)/);
  assert.match(tray, /ReadStartup\(writeError\);\s*Refresh\(\);/);
  assert.match(browser, /OnBeforeUninstallFastCallback[\s\S]+WindowsStartup\.Remove\(service\)[\s\S]+OnBeforeUpdateFastCallback/);
  assert.match(browser, /OnBeforeUpdateFastCallback\(_ => Storage\.Client\.ServiceLifetime\.StopBundled\(service\)\)/);
});

test('startup errors are refreshed on reopen and do not hide lifecycle errors', () => {
  const tray = read('apps/windows/Storage.Service/NativeTrayWindow.cs');
  assert.match(tray, /failure = uiError \?\? controller\.Error \?\? startupError;/);
  assert.match(tray, /private void ReadStartup\(string\? writeError = null\)\s*\{\s*startupError = writeError;/);
  assert.match(tray, /ApplyFonts\(\); Layout\(\); ReadStartup\(\); Refresh\(\);/);
  assert.match(tray, /catch \(Exception e\) when \(IsStartupError\(e\)\) \{ startupError \?\?= "Cannot read startup setting\."; \}/);
});
