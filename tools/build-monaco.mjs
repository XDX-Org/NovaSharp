import { createHash } from 'node:crypto';
import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const packageRoot = path.join(root, 'node_modules', 'monaco-editor');
const source = path.join(packageRoot, 'min', 'vs');
const destination = path.join(root, 'src', 'NovaSharp', 'wwwroot', 'monaco');
const manifestPath = path.join(destination, 'asset-manifest.json');
const packageJson = JSON.parse(await readFile(path.join(packageRoot, 'package.json'), 'utf8'));
const licenses = [
  ['monaco-editor', 'LICENSE', 'monaco-editor-MIT.txt'],
  ['dompurify', 'LICENSE', 'dompurify-Apache-2.0.txt'],
  ['dompurify', 'LICENSE-MPL', 'dompurify-MPL-2.0.txt'],
  ['marked', 'LICENSE.md', 'marked-MIT.md'],
  ['@types/trusted-types', 'LICENSE', 'trusted-types-MIT.txt']
];

async function filesUnder(directory, prefix = '') {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    const relative = path.join(prefix, entry.name);
    if (entry.isDirectory()) files.push(...await filesUnder(path.join(directory, entry.name), relative));
    else files.push(relative);
  }
  return files;
}

async function createManifest() {
  const files = {};
  for (const relative of await filesUnder(destination)) {
    if (relative === 'asset-manifest.json') continue;
    const contents = await readFile(path.join(destination, relative));
    files[relative.split(path.sep).join('/')] = createHash('sha256').update(contents).digest('hex');
  }
  return { monacoEditorVersion: packageJson.version, files };
}

if (process.argv.includes('--check')) {
  const expected = JSON.parse(await readFile(manifestPath, 'utf8'));
  const actual = await createManifest();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error('Generated Monaco assets do not match asset-manifest.json. Run npm run build:monaco.');
  }
} else {
  await rm(destination, { recursive: true, force: true });
  await mkdir(destination, { recursive: true });
  await cp(source, path.join(destination, 'vs'), { recursive: true });
  await mkdir(path.join(destination, 'licenses'));
  for (const [dependency, sourceName, destinationName] of licenses) {
    await cp(path.join(root, 'node_modules', dependency, sourceName), path.join(destination, 'licenses', destinationName));
  }
  const manifest = await createManifest();
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
}
