import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { build, version as esbuildVersion } from 'esbuild';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const nodeModules = path.join(root, 'node_modules');
const monacoRoot = path.join(nodeModules, 'monaco-editor');
const destination = path.join(root, 'src', 'NovaSharp', 'wwwroot', 'monaco');
const work = await mkdtemp(path.join(os.tmpdir(), 'novasharp-monaco-'));
const licenses = [
    ['monaco-editor', 'LICENSE', 'monaco-editor-MIT.txt'],
    ['dompurify', 'LICENSE', 'dompurify-Apache-2.0.txt'],
    ['dompurify', 'LICENSE-MPL', 'dompurify-MPL-2.0.txt'],
    ['marked', 'LICENSE.md', 'marked-MIT.md'],
    ['dompurify/node_modules/@types/trusted-types', 'LICENSE', 'trusted-types-MIT.txt']
];

async function filesUnder(directory, prefix = '') {
    const entries = await readdir(directory, { withFileTypes: true });
    const files = [];
    for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
        const relative = path.join(prefix, entry.name);
        if (entry.isDirectory()) {
            files.push(...await filesUnder(path.join(directory, entry.name), relative));
        } else {
            files.push(relative);
        }
    }
    return files;
}

async function createManifest(directory, versions) {
    const files = {};
    for (const relative of await filesUnder(directory)) {
        if (relative === 'asset-manifest.json') {
            continue;
        }
        const contents = await readFile(path.join(directory, relative));
        files[relative.split(path.sep).join('/')] = createHash('sha256').update(contents).digest('hex');
    }
    return {
        schemaVersion: 1,
        monacoEditorVersion: versions.monaco,
        esbuildVersion,
        files
    };
}

async function buildAssets() {
    const packageJson = JSON.parse(await readFile(path.join(monacoRoot, 'package.json'), 'utf8'));
    await Promise.all([
        build({
            entryPoints: [path.join(root, 'tools', 'monaco', 'entry.js')],
            outfile: path.join(work, 'monaco.js'),
            bundle: true,
            format: 'esm',
            platform: 'browser',
            target: 'es2022',
            minify: true,
            legalComments: 'none',
            loader: { '.ttf': 'file' },
            assetNames: 'assets/[name]-[hash]'
        }),
        build({
            entryPoints: [path.join(monacoRoot, 'esm', 'vs', 'editor', 'editor.worker.js')],
            outfile: path.join(work, 'editor.worker.js'),
            bundle: true,
            format: 'esm',
            platform: 'browser',
            target: 'es2022',
            minify: true,
            legalComments: 'none'
        })
    ]);

    const licenseDirectory = path.join(work, 'licenses');
    await mkdir(licenseDirectory, { recursive: true });
    for (const [dependency, sourceName, destinationName] of licenses) {
        await cp(path.join(nodeModules, dependency, sourceName), path.join(licenseDirectory, destinationName));
    }

    const manifest = await createManifest(work, { monaco: packageJson.version });
    await writeFile(path.join(work, 'asset-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
    return manifest;
}

try {
    const expected = await buildAssets();
    if (process.argv.includes('--check')) {
        const recorded = JSON.parse(await readFile(path.join(destination, 'asset-manifest.json'), 'utf8'));
        const actual = await createManifest(destination, { monaco: recorded.monacoEditorVersion });
        if (JSON.stringify(recorded) !== JSON.stringify(actual) || JSON.stringify(recorded) !== JSON.stringify(expected)) {
            throw new Error('Generated Monaco assets differ from the pinned inputs. Run npm run build:monaco.');
        }
        process.stdout.write('Monaco assets match the pinned ESM build.\n');
    } else {
        await rm(destination, { recursive: true, force: true });
        await mkdir(path.dirname(destination), { recursive: true });
        await cp(work, destination, { recursive: true });
        process.stdout.write(`Built Monaco ${expected.monacoEditorVersion} ESM assets in ${destination}.\n`);
    }
} finally {
    await rm(work, { recursive: true, force: true });
}
