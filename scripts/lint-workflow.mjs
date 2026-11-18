import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// GitHub documents concurrency.queue:max, but pinned actionlint 1.7.12 predates
// that key. Validate its one exact supported form ourselves; suppress ONLY the
// parser's unknown-queue diagnostic, never the rest of workflow validation.
// Reference: docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax
const block = '    concurrency:\n      group: storage-release-publication\n      cancel-in-progress: false\n      queue: max\n';
export function validateQueueSyntax(text) {
 text = text.replaceAll('\r\n', '\n');
 const queueKeys = [...text.matchAll(/^\s*["']?queue["']?\s*:/gm)];
 if (queueKeys.length !== 1 || text.split(block).length !== 2) throw new Error('Only the documented serialized release concurrency.queue:max form is supported.');
}
export function lintWorkflow({ workflow = fileURLToPath(new URL('../.github/workflows/ci.yml', import.meta.url)), actionlint = process.env.ACTIONLINT || 'actionlint', run = spawnSync } = {}) {
 const text = fs.readFileSync(workflow, 'utf8');
 validateQueueSyntax(text);
 const result = run(actionlint, ['-shellcheck=', '-pyflakes=', '-ignore', '^unexpected key "queue" for "concurrency" section\\.', workflow], { encoding: 'utf8', shell: false, timeout: 60_000, maxBuffer: 4 * 1024 * 1024 });
 if (result.error || result.status !== 0) throw new Error(`Workflow validation failed: ${result.error?.message || result.stdout || result.stderr || result.status}`);
 return result.stdout;
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
 try {
  const options = {};
  for (let i = 2; i < process.argv.length; i += 2) {
   const flag = process.argv[i], value = process.argv[i + 1];
   if (!['--actionlint', '--workflow'].includes(flag) || !value || Object.hasOwn(options, flag)) throw new Error('Usage: node scripts/lint-workflow.mjs [--actionlint executable] [--workflow file]');
   options[flag] = value;
  }
  process.stdout.write(lintWorkflow({ actionlint: options['--actionlint'], workflow: options['--workflow'] }));
  console.log('Workflow valid; documented queue:max checked separately for pinned actionlint compatibility.');
 } catch (error) { console.error(error.message); process.exitCode = 1; }
}
