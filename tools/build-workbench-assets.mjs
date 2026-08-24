import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const nodeModules = path.join(root, 'node_modules');
const codiconsRoot = path.join(nodeModules, '@vscode', 'codicons');
const interRoot = path.join(nodeModules, '@fontsource-variable', 'inter');
const fastMonoSource = path.join(root, 'assets', 'fonts', 'Fast_Mono.ttf');
const destination = path.join(root, 'src', 'NovaSharp', 'wwwroot', 'workbench-assets');
const work = await mkdtemp(path.join(os.tmpdir(), 'novasharp-workbench-'));
const fastMonoSha256 = '04cd57761e3855986c79724fd5e8f9105ba871b26ef2c795d7ce4f90284726b6';

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

async function createManifest(directory, versions) {
    const files = {};
    for (const relative of await filesUnder(directory)) {
        if (relative === 'asset-manifest.json') continue;
        const contents = await readFile(path.join(directory, relative));
        files[relative.split(path.sep).join('/')] = createHash('sha256').update(contents).digest('hex');
    }
    return { schemaVersion: 1, ...versions, files };
}

async function buildAssets() {
    const [codiconsPackage, interPackage] = await Promise.all([
        readFile(path.join(codiconsRoot, 'package.json'), 'utf8').then(JSON.parse),
        readFile(path.join(interRoot, 'package.json'), 'utf8').then(JSON.parse),
    ]);
    const sourceHash = createHash('sha256').update(await readFile(fastMonoSource)).digest('hex');
    if (sourceHash !== fastMonoSha256) {
        throw new Error(`Fast Mono source hash differs: ${sourceHash}`);
    }
    await Promise.all([
        cp(path.join(codiconsRoot, 'dist', 'codicon.css'), path.join(work, 'codicon.css')),
        cp(path.join(codiconsRoot, 'dist', 'codicon.ttf'), path.join(work, 'codicon.ttf')),
        cp(path.join(interRoot, 'files', 'inter-latin-wght-normal.woff2'), path.join(work, 'inter-latin.woff2')),
        cp(fastMonoSource, path.join(work, 'fast-mono.ttf')),
        cp(path.join(root, 'assets', 'brand', 'nova-mark.png'), path.join(work, 'nova-mark.png')),
    ]);
    await writeFile(path.join(work, 'fonts.css'), `@font-face {\n    font-family: "Inter Variable";\n    font-style: normal;\n    font-display: swap;\n    font-weight: 100 900;\n    src: url("./inter-latin.woff2") format("woff2-variations");\n}\n\n@font-face {\n    font-family: "Fast Mono";\n    font-style: normal;\n    font-display: swap;\n    font-weight: 400;\n    src: url("./fast-mono.ttf") format("truetype");\n}\n`);
    const licenseDirectory = path.join(work, 'licenses');
    await mkdir(licenseDirectory, { recursive: true });
    const oflTemplate = await readFile(path.join(interRoot, 'LICENSE'), 'utf8');
    const oflStart = oflTemplate.indexOf('This Font Software is licensed');
    if (oflStart < 0) throw new Error('The pinned OFL template no longer contains the expected license body.');
    const oflBody = oflTemplate.slice(oflStart);
    await Promise.all([
        cp(path.join(codiconsRoot, 'LICENSE'), path.join(licenseDirectory, 'codicons-CC-BY-4.0.txt')),
        cp(path.join(codiconsRoot, 'LICENSE-CODE'), path.join(licenseDirectory, 'codicons-code-MIT.txt')),
        cp(path.join(interRoot, 'LICENSE'), path.join(licenseDirectory, 'inter-OFL-1.1.txt')),
        writeFile(
            path.join(licenseDirectory, 'fast-mono-OFL-1.1.txt'),
            `Copyright 2014-2020 The Fira Code Project Authors (https://github.com/tonsky/FiraCode)\n\n${oflBody}`),
    ]);
    const manifest = await createManifest(work, {
        codiconsVersion: codiconsPackage.version,
        interVersion: interPackage.version,
        fastMonoVersion: '5.002',
        fastMonoSourceSha256: fastMonoSha256,
    });
    await writeFile(path.join(work, 'asset-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
    return manifest;
}

try {
    const expected = await buildAssets();
    if (process.argv.includes('--check')) {
        const recorded = JSON.parse(await readFile(path.join(destination, 'asset-manifest.json'), 'utf8'));
        const actual = await createManifest(destination, {
            codiconsVersion: recorded.codiconsVersion,
            interVersion: recorded.interVersion,
            fastMonoVersion: recorded.fastMonoVersion,
            fastMonoSourceSha256: recorded.fastMonoSourceSha256,
        });
        if (JSON.stringify(recorded) !== JSON.stringify(actual) || JSON.stringify(recorded) !== JSON.stringify(expected)) {
            throw new Error('Generated workbench assets differ from the pinned inputs. Run npm run build:workbench.');
        }
        process.stdout.write('Workbench assets match the pinned icon, font, and brand inputs.\n');
    } else {
        await rm(destination, { recursive: true, force: true });
        await mkdir(path.dirname(destination), { recursive: true });
        await cp(work, destination, { recursive: true });
        process.stdout.write(`Built Codicons ${expected.codiconsVersion}, Inter ${expected.interVersion}, and Fast Mono ${expected.fastMonoVersion} assets.\n`);
    }
} finally {
    await rm(work, { recursive: true, force: true });
}
