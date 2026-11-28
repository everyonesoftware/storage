import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
const root = new URL('../apps/windows/Storage.Browser/', import.meta.url);
const read = path => fs.readFileSync(new URL(path, root), 'utf8');
test('Settings uses the published shared package and one native update host without duplicate commands', () => {
  const xaml = read('Shell.xaml');
  assert.match(read('Storage.Browser.csproj'), /PackageReference Include="EveryoneSoftware.WindowsCommon" Version="0\.1\.6"/);
  assert.match(xaml, /<ContentControl[^>]*x:Name="UpdatesHost"[^>]*\/>/);
  assert.doesNotMatch(xaml, /x:Name="(?:CheckUpdates|DownloadUpdate|RestartUpdate|UpdateProgress|ApplicationVersion|UpdateStatus)"/);
  assert.doesNotMatch(xaml, /0\.1\.0|not connected to the release feed/);
});
test('Panel is attached after XAML load, disposed with window and routes user actions through controller', () => {
  const main = read('MainWindow.cs'), panel = read('WindowsUpdateSettings.cs');
  assert.match(main, /updates = new WindowsUpdateSettings\(root, DispatcherQueue, PrepareUpdateRestartAsync\)/);
  assert.match(main, /updates\.Dispose\(\)/);
  assert.match(main, /if \(busy\) throw new InvalidOperationException/);
  assert.doesNotMatch(main, /No network check was performed/);
  assert.match(panel, /AssemblyInformationalVersionAttribute/);
  for (const action of ['Check', 'Download', 'Restart']) {
    assert.match(panel, new RegExp(`panel\\.${action}Requested \\+= ${action}Clicked`));
    assert.match(panel, new RegExp(`controller\\.${action}Async\\(\\)`));
  }
  assert.match(panel, /new WindowsUpdatesPanel\("Storage", build.Split\('\+'\)\[0\]\)/);
  assert.match(panel, /Content = panel.View/);
  assert.match(panel, /WindowsUpdatePhase\.Downloading or WindowsUpdatePhase\.Verifying => UpdatePhase\.Downloading/);
  assert.match(panel, /restartAllowed: state.Phase == WindowsUpdatePhase.Ready/);
  assert.doesNotMatch(panel, /AllowRecheckDuringDownloadOrReady/);
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
  assert.match(xaml, /x:Name="SettingsContent"/);
  assert.match(xaml, /<Button[^>]*Grid.Row="2"[^>]*x:Name="SettingsNav"/);
  for (const label of ['All items', 'Collections', 'Settings']) assert.match(xaml, new RegExp(`AutomationProperties.Name="${label}"`));
  for (const name of ['InspectorHost', 'WorkspaceContent', 'WorkspaceColumns', 'ServiceRepository', 'ServiceDetails', 'TechnicalDetails', 'SecurityDetails']) assert.match(xaml, new RegExp(`x:Name="${name}"`));
  assert.match(main, /C<Grid>\("WorkspaceContent"\).RowSpacing = target == "settings" \? 0 : 18/);
  assert.match(main, /ColumnSpan\(C<Grid>\("WorkspaceContent"\), target == "settings" \? 2 : 1\)/);
  assert.match(main, /C<Border>\("InspectorHost"\).Visibility = target == "settings" \? Visibility.Collapsed : Visibility.Visible/);
  assert.match(main, /C<TextBlock>\("Status"\).Visibility = target == "settings" \? Visibility.Collapsed : Visibility.Visible/);
  assert.doesNotMatch(main, /\(string\).*\.Content/);
  for (const text of ['Updates', 'Local service', 'About Storage', 'Security and build information', 'Windows releases are unsigned', 'Open Devices from the Storage Service tray icon']) assert.ok(xaml.includes(text), text);
  assert.ok(!xaml.includes('Device pairing, replication'), 'the local-build notice must not deny the separate Windows enrollment workflow');
  assert.doesNotMatch(xaml, /WebView/);
});
test('Local service and About use shared chrome without losing app-owned details', () => {
  const xaml = read('Shell.xaml'), main = read('MainWindow.cs');
  for (const host of ['LocalServiceHost', 'AboutStorageHost']) {
    assert.match(xaml, new RegExp(`<ContentControl[^>]*x:Name="${host}"`));
    assert.match(main, new RegExp(`SetSettingsCard\\("${host}"`));
  }
  assert.match(main, /slots.Children.Clear\(\);\s+host.Content = SettingsCard.Create\(header, body, glyph\)/);
});
test('available notes use real on-demand disclosure and a separate retry without gating download', () => {
  const xaml = read('Shell.xaml'), panel = read('WindowsUpdateSettings.cs');
  for (const [tag, name] of [['Expander', 'WhatsNew'], ['Button', 'RetryChangelog'], ['StackPanel', 'NotesGroups'], ['Expander', 'CommitDetails']]) assert.match(xaml, new RegExp(`<${tag}[^>]*x:Name="${name}"`));
  assert.match(panel, /changelog: hasOffer \? whatsNew : null/);
  assert.match(xaml, /x:Name="WhatsNew"[^>]*IsExpanded="False"/);
  assert.match(panel, /Expanding \+= NotesExpanded/);
  assert.match(panel, /await notes.LoadAsync\(\)/);
  assert.match(panel, /notes.Select\(/);
  assert.match(panel, /notes.Dispose\(\)/);
  assert.match(panel, /ApplicationBuild/);
  assert.doesNotMatch(panel, /ReleaseNotes|ReleaseBody|Version .*→/);
});
test('startup autoapply stays disabled and exact bundled-service hooks survive', () => {
  const program = read('Program.cs');
  assert.match(program, /SetAutoApplyOnStartup\(false\)/);
  assert.match(program, /OnBeforeUninstallFastCallback\(_ =>\s*\{\s*try \{ Storage\.Client\.ServiceLifetime\.StopBundled\(service\); \}/);
  assert.match(program, /OnBeforeUpdateFastCallback\(_ => Storage\.Client\.ServiceLifetime\.StopBundled\(service\)\)/);
  assert.match(read('Storage.Browser.csproj'), /PackageReference Include="Velopack" Version="1.2.161"/);
});
