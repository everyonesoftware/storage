import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../apps/android');
const read = (path) => readFileSync(resolve(root, path), 'utf8');
const background = 'M8,4h32a4,4 0,0 1,4 4v32a4,4 0,0 1,-4 4H8a4,4 0,0 1,-4 -4V8a4,4 0,0 1,4 -4';
const family = {
  browser: {
    shape: 'M12,14h10l4,4h10v17H12z',
    details: 'M17,24h14v3H17zM17,29h9v2H17z',
  },
  service: {
    shape: 'M14,13h20a2,2 0,0 1,2 2v6a2,2 0,0 1,-2 2H14a2,2 0,0 1,-2 -2v-6a2,2 0,0 1,2 -2M14,26h20a2,2 0,0 1,2 2v6a2,2 0,0 1,-2 2H14a2,2 0,0 1,-2 -2v-6a2,2 0,0 1,2 -2',
    details: 'M16,17h8v2h-8zM16,30h8v2h-8zM32.5,18a1.5,1.5 0,1 0,-3 0a1.5,1.5 0,1 0,3 0M32.5,31a1.5,1.5 0,1 0,-3 0a1.5,1.5 0,1 0,3 0',
  },
};
const paths = (xml) => [...xml.matchAll(/<path\b([^>]+)\/>/g)].map(([, attributes]) => Object.fromEntries([...attributes.matchAll(/android:(\w+)="([^"]*)"/g)].map(([, key, value]) => [key, value])));

for (const [app, geometry] of Object.entries(family)) {
  test(`${app} canonical icon preserves approved 48-unit geometry and shared colors`, () => {
    const xml = read(`${app}/src/main/res/drawable/ic_storage.xml`);
    for (const dimension of ['width="48dp"', 'height="48dp"', 'viewportWidth="48"', 'viewportHeight="48"']) assert.ok(xml.includes(`android:${dimension}`));
    assert.deepEqual(paths(xml), [
      { fillColor: '#006B64', pathData: background },
      { fillColor: '#E8FFFC', pathData: geometry.shape },
      { fillColor: '#006B64', pathData: geometry.details },
    ]);
  });
}

for (const [app, geometry] of Object.entries(family)) {
  test(`${app} standard, round and themed adaptive icons resolve to the correct family`, () => {
    const manifest = read(`${app}/src/main/AndroidManifest.xml`);
    assert.match(manifest, /android:icon="@mipmap\/ic_launcher"/);
    assert.match(manifest, /android:roundIcon="@mipmap\/ic_launcher"/);
    for (const qualifier of ['mipmap-anydpi-v26', 'mipmap-anydpi-v33']) {
      const adaptive = read(`${app}/src/main/res/${qualifier}/ic_launcher.xml`);
      assert.match(adaptive, /<adaptive-icon\b/);
      assert.match(adaptive, /<background android:drawable="@color\/storage_icon_background"\s*\/>/);
      assert.match(adaptive, /<foreground android:drawable="@drawable\/ic_storage_foreground"\s*\/>/);
      if (qualifier.endsWith('v33')) assert.match(adaptive, /<monochrome android:drawable="@drawable\/ic_storage_monochrome"\s*\/>/);
      else assert.doesNotMatch(adaptive, /monochrome/);
    }
    assert.match(read(`${app}/src/main/res/values/icon_colors.xml`), /<color name="storage_icon_background">#006B64<\/color>/);
    for (const variant of ['foreground', 'monochrome']) {
      const xml = read(`${app}/src/main/res/drawable/ic_storage_${variant}.xml`);
      assert.match(xml, /android:viewportWidth="108" android:viewportHeight="108"/);
      assert.match(xml, /<group android:scaleX="2" android:scaleY="2" android:translateX="6" android:translateY="6">/);
      // The same glyph fits entirely in the adaptive mask's central safe area.
      if (variant === 'foreground') assert.deepEqual(paths(xml), [
        { fillColor: '#E8FFFC', pathData: geometry.shape },
        { fillColor: '#006B64', pathData: geometry.details },
      ]);
      else assert.deepEqual(paths(xml), [{ fillColor: '#FFFFFFFF', fillType: 'evenOdd', pathData: geometry.shape + geometry.details }]);
    }
    if (app === 'service') {
      const launchers = [...manifest.matchAll(/<activity\b([^>]*)>([\s\S]*?)<\/activity>/g)]
        .filter(([, , body]) => body.includes('android.intent.category.LAUNCHER'));
      assert.equal(launchers.length, 1, 'only the device pairing activity may be a launcher');
      assert.match(launchers[0][1], /android:name="\.pairing\.ui\.PairingActivity"/);
      assert.match(launchers[0][1], /android:exported="true"/);
      assert.match(launchers[0][2], /android:name="android\.intent\.action\.MAIN"/);
      assert.doesNotMatch(manifest, /<activity-alias\b/);
    }
  });
}

test('Browser title and empty states use untinted canonical folder branding, not a service glyph', () => {
  const source = read('browser/src/main/java/com/everyonesoftware/storage/browser/MainActivity.kt');
  assert.match(source, /import androidx.compose.foundation.Image/);
  assert.match(source, /import androidx.compose.ui.res.painterResource/);
  assert.match(source, /else Image\(painterResource\(R.drawable.ic_storage\), null, Modifier.padding\(start = 12.dp\).size\(36.dp\)\)/);
  const empty = source.slice(source.indexOf('@Composable private fun EmptyState'), source.indexOf('@Composable private fun ItemCard'));
  assert.match(empty, /Image\(painterResource\(R.drawable.ic_storage\), null, Modifier.size\(54.dp\)\)/);
  assert.doesNotMatch(empty, /Icons.Outlined.FolderOpen|tint\s*=/);
  assert.match(source, /"Items" to Icons.Outlined.Folder/, 'functional navigation remains a folder, not product branding');
});
