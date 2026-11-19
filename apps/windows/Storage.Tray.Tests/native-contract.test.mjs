import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = name => readFileSync(new URL(`../Storage.Service/${name}`, import.meta.url), 'utf8');
test('service is a portable GUI apphost with its own engine installation icon', () => {
  const project = source('Storage.Service.csproj');
  assert.match(project, /<OutputType>WinExe<\/OutputType>/);
  assert.match(project, /<TargetFramework>net10\.0<\/TargetFramework>/);
  assert.match(project, /<ApplicationIcon>Assets\/Storage.ico<\/ApplicationIcon>/);
  assert.match(project, /<Content Include="Assets\/Storage.ico" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest"/);
  assert.doesNotMatch(project, /Storage.Browser\/Assets/);
});
test('native adapter binds accessible native buttons, Shell v4, Explorer recovery and keyboard navigation', () => {
  const native = source('NativeTrayWindow.cs');
  for (const name of ['Open Storage', 'Start service', 'Stop service', 'Restart service', 'Quit Storage Service']) assert.ok(native.includes(name), name);
  for (const api of ['Shell_NotifyIcon', 'TaskbarCreated', 'IsDialogMessage', 'GetMonitorInfo', 'WM_DPICHANGED', 'DrawFocusRect', 'TTM_ADDTOOL', 'NIN_SELECT', 'NIN_KEYSELECT', 'WM_CONTEXTMENU']) assert.ok(native.includes(api), api);
  assert.match(native, /CreateWindowEx\([^\n]*"Button"/);
  assert.match(native, /ArgumentList\.Add\("--from-tray"\)/);
  assert.match(native, /UseShellExecute = false/);
  assert.doesNotMatch(native, /WebView|Windows\.Forms|WM_LBUTTONUP/);
});
test('production readiness follows successful pipe creation and quit keeps installation event', () => {
  const program = source('Program.cs');
  const host = source('WindowsHost.cs');
  assert.match(program, /\[STAThread\]/);
  assert.match(program, /ServiceLifetime\.StopEventName\(Environment\.ProcessPath!\)/);
  assert.match(program, /ServiceLifetime\.StopBundled\(Environment\.ProcessPath!\)/);
  assert.match(program, /using var core = new NativeCore/);
  assert.match(host, /CreateLocalPipe\(\);\s*ready\?\.Invoke\(\)/);
});
