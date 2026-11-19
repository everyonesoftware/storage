import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
const root = new URL('../apps/windows/Storage.Browser/', import.meta.url);
const read = path => fs.readFileSync(new URL(path, root), 'utf8');
test('Settings exposes named native update actions, progress, live status and actual assembly version', () => {
  const xaml = read('Shell.xaml');
  for (const [tag, name] of [['Button', 'CheckUpdates'], ['Button', 'DownloadUpdate'], ['Button', 'RestartUpdate'], ['ProgressBar', 'UpdateProgress'], ['TextBlock', 'ApplicationVersion']])
    assert.match(xaml, new RegExp(`<${tag}[^>]*x:Name="${name}"`));
  assert.match(xaml, /x:Name="UpdateStatus"[^>]*AutomationProperties.LiveSetting="Polite"/);
  assert.doesNotMatch(xaml, /0\.1\.0|not connected to the release feed/);
});
test('Panel is attached after XAML load, disposed with window and routes user actions through controller', () => {
  const main = read('MainWindow.cs'), panel = read('WindowsUpdatesPanel.cs');
  assert.match(main, /updates = new WindowsUpdatesPanel\(root, DispatcherQueue, PrepareUpdateRestartAsync\)/);
  assert.match(main, /updates\.Dispose\(\)/);
  assert.match(main, /if \(busy\) throw new InvalidOperationException/);
  assert.doesNotMatch(main, /No network check was performed/);
  assert.match(panel, /AssemblyInformationalVersionAttribute/);
  for (const action of ['CheckAsync', 'DownloadAsync', 'RestartAsync']) assert.match(panel, new RegExp(`controller\\.${action}\\(\\)`));
  assert.match(panel, /Render\(controller.Status\)/);
  assert.match(panel, /TryEnqueue/);
  assert.doesNotMatch(panel, /\.Parent/);
});
test('existing native project harness runs real updater behavior tests', () => {
  assert.match(read('../Storage.Tests/Storage.Tests.csproj'), /ProjectReference Include="\.\.\/Storage.Update.Tests\/Storage.Update.Tests.csproj"/);
  assert.match(read('../Storage.Tests/Program.cs'), /await Storage.Update.Tests.UpdateTestSuite.Run\(\)/);
});
test('native harness includes cached-package integrity behaviors against the actual backend and SDK locator', () => {
  assert.match(read('../Storage.Update.Tests/Program.cs'), /await CachedPackageTests\.Run\(\)/);
  const behavior = read('../Storage.Update.Tests/CachedPackageTests.cs');
  assert.match(behavior, /new WindowsUpdateBackend\(new WindowsGithubSource\(Downloader\), Locator\)/);
  assert.match(behavior, /RecordingLocator : TestVelopackLocator, IProcessImpl/);
  for (const behaviorName of ['ValidPackage', 'RetamperedPackage', 'RetamperedController'])
    assert.match(behavior, new RegExp(`await ${behaviorName}\\(`));
});
test('Settings reclaims the inspector column, anchors icon navigation, and groups compact native content', () => {
  const xaml = read('Shell.xaml'), main = read('MainWindow.cs');
  assert.match(xaml, /x:Name="SettingsContent"[^>]*MaxWidth="720"/);
  assert.match(xaml, /<Button[^>]*Grid.Row="2"[^>]*x:Name="SettingsNav"/);
  for (const label of ['All items', 'Collections', 'Settings']) assert.match(xaml, new RegExp(`AutomationProperties.Name="${label}"`));
  for (const name of ['InspectorHost', 'WorkspaceContent', 'WorkspaceColumns', 'ServiceRepository', 'ServiceDetails', 'TechnicalDetails', 'SecurityDetails']) assert.match(xaml, new RegExp(`x:Name="${name}"`));
  assert.match(main, /C<Grid>\("WorkspaceContent"\).RowSpacing = target == "settings" \? 0 : 18/);
  assert.match(main, /ColumnSpan\(C<Grid>\("WorkspaceContent"\), target == "settings" \? 2 : 1\)/);
  assert.match(main, /C<Border>\("InspectorHost"\).Visibility = target == "settings" \? Visibility.Collapsed : Visibility.Visible/);
  assert.match(main, /C<TextBlock>\("Status"\).Visibility = target == "settings" \? Visibility.Collapsed : Visibility.Visible/);
  assert.doesNotMatch(main, /\(string\).*\.Content/);
  for (const text of ['Updates', 'Local service', 'About Storage', 'Security and build information', 'Windows releases are unsigned', 'Device pairing, replication']) assert.ok(xaml.includes(text), text);
  assert.doesNotMatch(xaml, /WebView/);
});
test('available notes use real on-demand disclosure and a separate retry without gating download', () => {
  const xaml = read('Shell.xaml'), panel = read('WindowsUpdatesPanel.cs');
  for (const [tag, name] of [['TextBlock', 'UpdateTitle'], ['TextBlock', 'OfferedVersion'], ['Expander', 'WhatsNew'], ['Button', 'RetryChangelog'], ['StackPanel', 'NotesGroups'], ['Expander', 'CommitDetails']]) assert.match(xaml, new RegExp(`<${tag}[^>]*x:Name="${name}"`));
  assert.match(xaml, /x:Name="WhatsNew"[^>]*IsExpanded="False"/);
  assert.match(panel, /Expanding \+= NotesExpanded/);
  assert.match(panel, /await notes.LoadAsync\(\)/);
  assert.match(panel, /notes.Select\(/);
  assert.match(panel, /notes.Dispose\(\)/);
  assert.match(panel, /Current Version: /);
  assert.match(panel, /ApplicationBuild/);
  assert.doesNotMatch(panel, /ReleaseNotes|ReleaseBody|Version .*→/);
});
test('startup autoapply stays disabled and exact bundled-service hooks survive', () => {
  const program = read('Program.cs');
  assert.match(program, /SetAutoApplyOnStartup\(false\)/);
  for (const hook of ['OnBeforeUninstallFastCallback', 'OnBeforeUpdateFastCallback'])
    assert.match(program, new RegExp(`${hook}\\(_ => Storage\\.Client\\.ServiceLifetime\\.StopBundled\\(service\\)\\)`));
  assert.match(read('Storage.Browser.csproj'), /PackageReference Include="Velopack" Version="1.2.161"/);
});
