import { createServer } from 'node:http';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import pixelmatch from 'pixelmatch';
import pngjs from 'pngjs';
import { chromium, webkit } from 'playwright';

const { PNG } = pngjs;
const here = path.dirname(fileURLToPath(import.meta.url));
const wwwroot = path.resolve(here, '..', '..', 'src', 'NovaSharp', 'wwwroot');
const baselines = path.join(here, 'baselines');
const updateBaselines = process.env.NOVASHARP_UPDATE_BASELINES === '1';
const results = [];
const engineVersions = {};

const FIXTURE = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <link rel="stylesheet" href="/workbench-assets/fonts.css">
  <link rel="stylesheet" href="/workbench-assets/codicon.css">
  <link rel="stylesheet" href="/app.css">
  <style>
    [hidden] { display: none !important; }
    .fixture-code { grid-area: 2 / 1; overflow: hidden; padding: 18px 26px; background: var(--surface-editor); color: #bdc6d8; font: 13px/1.7 monospace; }
    .fixture-code .keyword { color: #c17cff; } .fixture-code .type { color: #79c0ff; } .fixture-code .comment { color: #70ad64; }
    .fixture-code p { margin: 0; white-space: pre; }
  </style>
</head>
<body>
<main class="workbench">
  <header class="command-bar" role="region" aria-label="Global command bar">
    <div class="brand" aria-label="NovaSharp"><img src="/workbench-assets/nova-mark.png" alt=""><span>NovaSharp</span></div>
    <nav class="global-menus" aria-label="Application menus">
      <div class="command-menu" data-command-menu="File"><button class="command-menu-trigger" aria-haspopup="menu" aria-expanded="false" aria-label="File menu">File</button></div>
      <div class="command-menu" data-command-menu="Workspace"><button class="command-menu-trigger" aria-haspopup="menu" aria-expanded="false" aria-label="Workspace menu">Workspace</button></div>
      <div class="command-menu" data-command-menu="View"><button class="command-menu-trigger" aria-haspopup="menu" aria-expanded="false" aria-label="View menu">View</button></div>
    </nav>
    <div class="command-menu global-overflow-menu"><button class="command-menu-trigger" aria-label="Application menu"><i class="codicon codicon-menu"></i></button></div>
    <div class="command-bar-actions" aria-label="File actions"><button class="command-button" aria-label="Open file" data-reachable><i class="codicon codicon-folder-opened"></i></button><button class="command-button" aria-label="Save" data-reachable><i class="codicon codicon-save"></i></button></div>
  </header>
  <div class="workbench-body">
    <div class="editor-workspace">
      <section class="editor-panel">
        <div class="tabs-bar"><div class="tabs-strip" role="tablist" aria-label="Open editors">
          <div class="document-tab active" role="tab" aria-selected="true"><span class="tab-label">SupersedingOperation.cs</span><button class="tab-close" aria-label="Close SupersedingOperation.cs"><i class="codicon codicon-close"></i></button></div>
          <div class="document-tab" role="tab" aria-selected="false"><i class="codicon codicon-circle-filled tab-state"></i><span class="tab-label">BoundedWorkQueue.cs</span><button class="tab-close" aria-label="Close BoundedWorkQueue.cs"><i class="codicon codicon-close"></i></button></div>
          <div class="document-tab preview" role="tab" aria-selected="false"><span class="tab-label">WorkspaceExplorer.razor</span><button class="tab-close" aria-label="Close WorkspaceExplorer.razor"><i class="codicon codicon-close"></i></button></div>
          <div class="document-tab" role="tab" aria-selected="false"><span class="tab-label">app.css</span><button class="tab-close" aria-label="Close app.css"><i class="codicon codicon-close"></i></button></div>
        </div></div>
        <div class="notice conflict" role="alert"><i class="codicon codicon-warning"></i><span>One file changed on disk.</span><button>Compare</button><button class="dismiss" aria-label="Dismiss"><i class="codicon codicon-close"></i></button></div>
        <div class="fixture-code" aria-label="Editor fixture"><p><span class="keyword">namespace</span> NovaSharp.Async;</p><p></p><p><span class="comment">/// Keeps one supersedable operation alive.</span></p><p><span class="keyword">public sealed class</span> <span class="type">SupersedingOperation</span></p><p>{</p><p>    <span class="keyword">private</span> readonly Lock _gate = new();</p><p>}</p></div>
      </section>
      <section class="bottom-panel" aria-label="Bottom panel" hidden><header><strong>Panel</strong><span>Problems, output, terminal, and debug</span></header></section>
    </div>
    <aside class="explorer" style="width:280px" aria-label="Explorer">
      <header class="explorer-header"><strong>Explorer</strong><button aria-label="Open folder"><i class="codicon codicon-folder-opened"></i></button><button aria-label="Refresh"><i class="codicon codicon-refresh"></i></button><button aria-label="Collapse all folders" data-reachable><i class="codicon codicon-collapse-all"></i></button><button aria-label="Close Explorer" data-reachable><i class="codicon codicon-close"></i></button></header>
      <div class="workspace-path">D:/Repos/NovaSharp</div>
      <div class="workspace-tree" role="tree">
        <div class="tree-item" data-kind="directory" data-root="true"><button class="tree-row" style="padding-left:8px"><span class="tree-twist"><i class="codicon codicon-chevron-down"></i></span><i class="codicon codicon-folder tree-icon"></i><span class="tree-name">NovaSharp</span></button></div>
        <div class="tree-item" data-kind="directory"><button class="tree-row" style="padding-left:24px"><span class="tree-twist"><i class="codicon codicon-chevron-down"></i></span><i class="codicon codicon-folder tree-icon"></i><span class="tree-name">Async</span></button></div>
        <div class="tree-item selected" data-kind="file"><button class="tree-row" style="padding-left:40px"><span class="tree-twist"></span><i class="codicon codicon-file-code tree-icon"></i><span class="tree-name">SupersedingOperation.cs</span></button></div>
        <div class="tree-item" data-kind="file"><button class="tree-row" style="padding-left:40px"><span class="tree-twist"></span><i class="codicon codicon-file-code tree-icon"></i><span class="tree-name">BoundedWorkQueue.cs</span></button></div>
        <div class="tree-item" data-kind="directory"><button class="tree-row" style="padding-left:24px"><span class="tree-twist"><i class="codicon codicon-chevron-right"></i></span><i class="codicon codicon-folder tree-icon"></i><span class="tree-name">Components</span></button></div>
        <div class="tree-item" data-kind="file"><button class="tree-row" style="padding-left:24px"><span class="tree-twist"></span><i class="codicon codicon-file-code tree-icon"></i><span class="tree-name">Workbench.cs</span></button></div>
      </div>
      <div class="explorer-resizer" role="separator" aria-label="Resize Explorer" aria-valuenow="280" tabindex="0"></div>
    </aside>
    <nav class="activity-rail" aria-label="Activity"><button class="activity-item active" aria-label="Explorer" data-reachable><i class="codicon codicon-files"></i></button></nav>
  </div>
  <footer class="status-bar" role="region" aria-label="Status"><button class="status-item">LF</button><button class="status-item">UTF-8</button><span class="status-item">4 spaces</span></footer>
  <div class="palette-scrim" hidden><section class="command-palette"><label class="command-palette-search"><i class="codicon codicon-search-sparkle"></i><input aria-label="Search commands" placeholder="Type a command"></label><div class="command-palette-results"><button><span class="command-category">File</span><span>Open File…</span><kbd>CtrlCmd+O</kbd></button><button><span class="command-category">View</span><span>Toggle Bottom Panel</span><kbd>CtrlCmd+J</kbd></button></div></section></div>
</main>
<script type="module">
  await import('/workbench-shell.js');
  await import('/workspace-explorer.js');
  await import('/editor-groups.js');
  globalThis.invoked = [];
  globalThis.resizeCommits = [];
  globalThis.editorGroupCommits = [];
  globalThis.editorGroupDrops = [];
  globalThis.mountEditorGroupsFixture = () => {
    document.getElementById('editor-groups-fixture')?.remove();
    const root = document.createElement('div');
    root.id = 'editor-groups-fixture';
    root.className = 'editor-groups-root';
    root.style.cssText = 'position:fixed;left:10px;top:70px;width:min(520px,calc(100vw - 20px));height:min(280px,calc(100vh - 90px));z-index:30;';
    root.innerHTML = '<div class="editor-split horizontal" style="--split-first:50%"><section class="editor-group active"><div class="tabs-bar"><div class="tabs-strip" role="tablist"><div class="document-tab active" role="tab" draggable="true">First.cs</div></div></div><div class="editor-group-host"></div><div class="group-drop-zone left"></div><div class="group-drop-zone right"></div><div class="group-drop-zone up"></div><div class="group-drop-zone down"></div><div class="group-drop-zone center"></div></section><div class="editor-splitter" role="separator" tabindex="0" aria-label="Resize editor groups" aria-orientation="vertical" aria-valuemin="10" aria-valuemax="90" aria-valuenow="50"></div><section class="editor-group"><div class="tabs-bar"><div class="tabs-strip" role="tablist"><div class="document-tab" role="tab" draggable="true">Second.cs</div></div></div><div class="editor-group-host"></div><div class="group-drop-zone left"></div><div class="group-drop-zone right"></div><div class="group-drop-zone up"></div><div class="group-drop-zone down"></div><div class="group-drop-zone center"></div></section></div>';
    document.body.append(root);
    NovaEditorGroups.attachDragSurface(root);
    const splitter = root.querySelector('.editor-splitter');
    NovaEditorGroups.attachSplitter(splitter, {
      async invokeMethodAsync(_name, splitId, ratio) { editorGroupCommits.push({ splitId, ratio }); }
    }, 'fixture-split', 'horizontal', 0.5);
    splitter.addEventListener('keydown', event => {
      const delta = event.key === 'ArrowLeft' ? -5 : event.key === 'ArrowRight' ? 5 : 0;
      if (!delta) return;
      const ratio = Math.max(10, Math.min(90, Number(splitter.getAttribute('aria-valuenow')) + delta));
      splitter.setAttribute('aria-valuenow', String(ratio));
      root.firstElementChild.style.setProperty('--split-first', ratio + '%');
    });
    for (const zone of root.querySelectorAll('.group-drop-zone')) {
      zone.addEventListener('dragover', event => event.preventDefault());
      zone.addEventListener('drop', event => { event.preventDefault(); editorGroupDrops.push(zone.classList[1]); });
    }
    root.querySelector('.tabs-strip').addEventListener('dragover', event => event.preventDefault());
    root.querySelector('.tabs-strip').addEventListener('drop', event => { event.preventDefault(); editorGroupDrops.push('center'); });
    return root;
  };
  const paletteResults = document.querySelector('.command-palette-results');
  for (let index = 1; index <= 24; index++) {
    const command = document.createElement('button');
    command.innerHTML = '<span class="command-category">View</span><span>Fixture command ' + index + '</span>';
    paletteResults.append(command);
  }
  const bridge = { async invokeMethodAsync(_name, commandId) {
    invoked.push(commandId);
    if (commandId === 'palette') { const scrim = document.querySelector('.palette-scrim'); scrim.hidden = false; scrim.querySelector('input').focus(); }
    if (commandId === 'panel') document.querySelector('.bottom-panel').hidden = !document.querySelector('.bottom-panel').hidden;
    if (commandId === 'explorer') {
      const explorer = document.querySelector('.explorer');
      if (explorer.hidden) { explorer.hidden = false; NovaWorkspace.restoreFocus(); }
      else { NovaWorkspace.rememberFocus(); explorer.hidden = true; document.querySelector('.activity-item').focus(); }
    }
  } };
  NovaWorkbench.initialize(bridge, [
    { id: 'palette', keybindings: ['CtrlCmd+Shift+KeyP'] },
    { id: 'panel', keybindings: ['CtrlCmd+KeyJ'] },
    { id: 'explorer', keybindings: ['CtrlCmd+KeyB'] },
  ], 'palette');
  NovaWorkspace.attachResizer(document.querySelector('.explorer-resizer'), {
    async invokeMethodAsync(_name, width) { resizeCommits.push(width); }
  });
  document.querySelector('[aria-label="Collapse all folders"]').addEventListener('click', () => {
    for (const item of document.querySelectorAll('.workspace-tree .tree-item:not([data-root])')) item.hidden = true;
  });
  const closeTopMenus = () => {
    for (const menu of document.querySelectorAll('[data-command-menu]')) {
      menu.querySelector('.command-menu-popup')?.remove();
      menu.querySelector('.command-menu-trigger').setAttribute('aria-expanded', 'false');
    }
  };
  document.addEventListener('pointerdown', event => {
    if (!event.target.closest('[data-command-menu]')) closeTopMenus();
  });
  for (const menu of document.querySelectorAll('[data-command-menu]')) {
    const trigger = menu.querySelector('.command-menu-trigger');
    trigger.addEventListener('click', () => {
      const wasOpen = trigger.getAttribute('aria-expanded') === 'true';
      closeTopMenus();
      if (wasOpen) return;
      const popup = document.createElement('div');
      popup.className = 'command-menu-popup';
      popup.setAttribute('role', 'menu');
      popup.setAttribute('aria-label', menu.dataset.commandMenu + ' menu');
      const item = document.createElement('button');
      item.setAttribute('role', 'menuitem');
      item.textContent = menu.dataset.commandMenu + ' command';
      popup.append(item);
      menu.append(popup);
      trigger.setAttribute('aria-expanded', 'true');
    });
    trigger.addEventListener('contextmenu', event => {
      event.preventDefault();
      closeTopMenus();
    });
  }
  const closeExplorerContextMenu = () => document.querySelectorAll('.explorer-context-menu, .explorer-context-scrim').forEach(element => element.remove());
  for (const row of document.querySelectorAll('.workspace-tree .tree-row')) {
    row.addEventListener('contextmenu', event => {
      event.preventDefault();
      closeExplorerContextMenu();
      const item = row.closest('.tree-item');
      const labels = item.dataset.kind === 'directory' ? ['New file', 'New folder'] : [];
      if (!item.dataset.root) labels.push('Move', 'Rename', 'Delete');
      const scrim = document.createElement('div');
      scrim.className = 'context-menu-scrim explorer-context-scrim';
      scrim.addEventListener('click', closeExplorerContextMenu);
      scrim.addEventListener('contextmenu', event => {
        event.preventDefault();
        closeExplorerContextMenu();
      });
      const menu = document.createElement('div');
      menu.className = 'context-command-menu explorer-context-menu';
      menu.setAttribute('role', 'menu');
      menu.setAttribute('aria-label', 'Actions for ' + row.querySelector('.tree-name').textContent);
      menu.tabIndex = -1;
      menu.style.setProperty('--context-x', String(event.clientX) + 'px');
      menu.style.setProperty('--context-y', String(event.clientY) + 'px');
      for (const label of labels) {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('role', 'menuitem');
        const text = document.createElement('span');
        text.textContent = label;
        button.append(text);
        menu.append(button);
      }
      document.querySelector('.workbench').append(scrim, menu);
      menu.focus();
    });
  }
  globalThis.shellReady = true;
</script>
</body>
</html>`;

function check(name, condition, detail = '') {
    results.push({ name, ok: Boolean(condition), detail });
    process.stdout.write(`  ${condition ? 'PASS' : 'FAIL'}  ${name}${condition || !detail ? '' : ` — ${detail}`}\n`);
}

async function compare(name, actualBuffer) {
    const baselinePath = path.join(baselines, `${name}.png`);
    if (updateBaselines) {
        await mkdir(baselines, { recursive: true });
        await writeFile(baselinePath, actualBuffer);
        check(`visual baseline ${name}`, true, 'updated');
        return;
    }
    let expectedBuffer;
    try { expectedBuffer = await readFile(baselinePath); }
    catch { check(`visual baseline ${name}`, false, 'missing baseline'); return; }
    const actual = PNG.sync.read(actualBuffer);
    const expected = PNG.sync.read(expectedBuffer);
    if (actual.width !== expected.width || actual.height !== expected.height) {
        check(`visual baseline ${name}`, false, `${actual.width}x${actual.height} != ${expected.width}x${expected.height}`);
        return;
    }
    const different = pixelmatch(expected.data, actual.data, null, actual.width, actual.height, { threshold: 0.2 });
    const ratio = different / (actual.width * actual.height);
    check(`visual baseline ${name}`, ratio <= 0.05, `${(ratio * 100).toFixed(2)}% pixels differ`);
}

function startServer() {
    const server = createServer(async (request, response) => {
        const url = new URL(request.url, 'http://localhost');
        if (url.pathname === '/') { response.writeHead(200, { 'content-type': 'text/html' }); response.end(FIXTURE); return; }
        const resolved = path.resolve(wwwroot, `.${url.pathname}`);
        if (!resolved.startsWith(wwwroot + path.sep)) { response.writeHead(403).end(); return; }
        try {
            const body = await readFile(resolved);
            const type = { '.css': 'text/css', '.js': 'text/javascript', '.png': 'image/png', '.ttf': 'font/ttf', '.woff2': 'font/woff2' }[path.extname(resolved)] ?? 'application/octet-stream';
            response.writeHead(200, { 'content-type': type }); response.end(body);
        } catch { response.writeHead(404).end(); }
    });
    return new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve({ server, port: server.address().port })));
}

async function capture(browser, origin, engine, name, viewport, deviceScaleFactor = 1, forcedColors = 'none') {
    const context = await browser.newContext({ viewport, deviceScaleFactor, forcedColors });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(origin);
    await page.waitForFunction(() => globalThis.shellReady);
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(50);
    check(`${engine} ${name} raises no script error`, errors.length === 0, errors.join('; '));
    const layout = await page.evaluate(() => ({
        narrow: document.querySelector('.workbench').classList.contains('narrow'),
        workbenchWidth: document.querySelector('.workbench').getBoundingClientRect().width,
        docked: (() => {
            const editor = document.querySelector('.editor-workspace').getBoundingClientRect();
            const explorer = document.querySelector('.explorer').getBoundingClientRect();
            const activity = document.querySelector('.activity-rail').getBoundingClientRect();
            return getComputedStyle(document.querySelector('.explorer')).position !== 'absolute'
                && editor.right <= explorer.left + 0.5
                && explorer.right <= activity.left + 0.5;
        })(),
        horizontalOverflowHidden: ['.editor-workspace', '.editor-panel', '.explorer', '.workspace-tree', '.bottom-panel']
            .every(selector => getComputedStyle(document.querySelector(selector)).overflowX === 'hidden'),
        tabScrollbarHidden: getComputedStyle(document.querySelector('.tabs-strip')).scrollbarWidth === 'none',
        clipped: Array.from(document.querySelectorAll('[data-reachable]')).filter(element => {
            const box = element.getBoundingClientRect();
            return box.left < 0 || box.top < 0 || box.right > innerWidth || box.bottom > innerHeight;
        }).length,
    }));
    const editorGroups = await page.evaluate(() => {
        const root = mountEditorGroupsFixture();
        const bounds = root.getBoundingClientRect();
        const splitter = root.querySelector('.editor-splitter').getBoundingClientRect();
        const edge = root.querySelector('.group-drop-zone.left').getBoundingClientRect();
        const result = {
            bounded: bounds.left >= 0 && bounds.right <= innerWidth && bounds.top >= 0 && bounds.bottom <= innerHeight,
            splitterPhysicalPixels: splitter.width * devicePixelRatio,
            edgeWidth: edge.width,
            separatorCount: root.querySelectorAll('[role="separator"][tabindex="0"]').length,
        };
        NovaEditorGroups.detachSplitter(root.querySelector('.editor-splitter'));
        NovaEditorGroups.detachDragSurface(root);
        root.remove();
        return result;
    });
    check(`${engine} ${name} keeps global controls reachable`, layout.clipped === 0, JSON.stringify(layout));
    check(`${engine} ${name} keeps Explorer docked on the right`, layout.docked, JSON.stringify(layout));
    check(`${engine} ${name} hides horizontal panel scrollbars`,
        layout.horizontalOverflowHidden && layout.tabScrollbarHidden, JSON.stringify(layout));
    if (viewport.width < 720) check(`${engine} ${name} uses the narrow shell`, layout.narrow, JSON.stringify(layout));
    check(`${engine} ${name} keeps split controls usable`,
        editorGroups.bounded && editorGroups.splitterPhysicalPixels >= 5
            && editorGroups.edgeWidth >= 30 && editorGroups.separatorCount === 1,
        JSON.stringify(editorGroups));
    await compare(`${engine}-${name}`, await page.screenshot({ animations: 'disabled' }));
    await context.close();
}

const { server, port } = await startServer();
const origin = `http://127.0.0.1:${port}`;
try {
    for (const [engine, browserType] of [['chromium', chromium], ['webkit', webkit]]) {
        const launchOptions = engine === 'chromium' && process.env.NOVASHARP_CHROMIUM_PATH
            ? { executablePath: process.env.NOVASHARP_CHROMIUM_PATH }
            : {};
        const browser = await browserType.launch(launchOptions);
        engineVersions[engine] = browser.version();
        try {
            await capture(browser, origin, engine, 'standard', { width: 1200, height: 800 });
            await capture(browser, origin, engine, 'narrow', { width: 640, height: 480 });
            await capture(browser, origin, engine, 'high-dpi', { width: 1200, height: 800 }, 2);
            await capture(browser, origin, engine, 'zoom-200', { width: 320, height: 240 }, 2);
            await capture(browser, origin, engine, 'high-contrast', { width: 1200, height: 800 }, 1, 'active');

            const context = await browser.newContext({ viewport: { width: 1200, height: 800 } });
            const page = await context.newPage();
            await page.goto(origin);
            await page.waitForFunction(() => globalThis.shellReady);
            await page.keyboard.press('Control+Shift+p');
            await page.waitForFunction(() => globalThis.invoked.includes('palette'));
            check(`${engine} palette shortcut works with the command bar`, await page.locator('.palette-scrim').isVisible());
            check(`${engine} palette shortcut focuses search`, await page.locator('.command-palette input').evaluate(element => element === document.activeElement));
            const paletteInvocations = await page.evaluate(() => {
                document.querySelector('.palette-scrim').hidden = true;
                return globalThis.invoked.filter(id => id === 'palette').length;
            });
            await page.keyboard.press('Shift');
            await page.keyboard.press('Shift');
            await page.waitForFunction(count => globalThis.invoked.filter(id => id === 'palette').length > count, paletteInvocations);
            check(`${engine} double Shift opens the command palette`, await page.locator('.palette-scrim').isVisible());
            check(`${engine} command palette receives focus`, await page.locator('.command-palette input').evaluate(element => element === document.activeElement));
            const paletteResults = page.locator('.command-palette-results');
            const paletteOverflow = await paletteResults.evaluate(element => element.scrollHeight > element.clientHeight);
            await paletteResults.hover();
            await page.mouse.wheel(0, 480);
            await page.waitForTimeout(50);
            check(`${engine} command palette results scroll after double Shift`,
                paletteOverflow && await paletteResults.evaluate(element => element.scrollTop > 0));
            await compare(`${engine}-palette`, await page.screenshot({ animations: 'disabled' }));
            await page.evaluate(() => {
                document.querySelector('.palette-scrim').hidden = true;
                document.querySelector('.activity-item').focus();
            });
            await page.keyboard.press('Control+j');
            await page.waitForFunction(() => globalThis.invoked.includes('panel'));
            check(`${engine} registry keybindings work outside Monaco`, await page.locator('.bottom-panel').isVisible());
            const accessibleRegions = {
                commands: await page.getByRole('region', { name: 'Global command bar' }).count(),
                activity: await page.getByRole('navigation', { name: 'Activity' }).count(),
                tabs: await page.getByRole('tab').count(),
                alerts: await page.getByRole('alert').count(),
                resize: await page.getByRole('separator', { name: 'Resize Explorer' }).count(),
                panel: await page.getByRole('region', { name: 'Bottom panel' }).count(),
                status: await page.getByRole('region', { name: 'Status' }).count(),
            };
            check(`${engine} exposes labelled command, activity, tabs, alerts, resize, panel, and status regions`,
                JSON.stringify(accessibleRegions) === JSON.stringify({ commands: 1, activity: 1, tabs: 4, alerts: 1, resize: 1, panel: 1, status: 1 }),
                JSON.stringify(accessibleRegions));
            await page.getByRole('button', { name: 'File menu' }).click();
            await page.getByRole('button', { name: 'Workspace menu' }).click();
            check(`${engine} opening a top menu closes the previous one`,
                await page.getByRole('menu', { name: 'File menu' }).count() === 0
                    && await page.getByRole('menu', { name: 'Workspace menu' }).count() === 1);
            await page.locator('.fixture-code').click({ button: 'right', position: { x: 4, y: 4 } });
            check(`${engine} right-click outside closes the top menu`, await page.getByRole('menu').count() === 0);
            await page.getByRole('button', { name: 'View menu' }).click();
            await page.locator('.fixture-code').click({ position: { x: 4, y: 4 } });
            check(`${engine} left-click outside closes the top menu`, await page.getByRole('menu').count() === 0);
            await page.getByRole('button', { name: 'Collapse all folders' }).click();
            const visibleAfterCollapse = await page.locator('.workspace-tree .tree-item:not([data-root]):visible').count();
            check(`${engine} Collapse All closes every Explorer folder`, visibleAfterCollapse === 0, String(visibleAfterCollapse));
            await page.evaluate(() => {
                for (const item of document.querySelectorAll('.workspace-tree .tree-item')) item.hidden = false;
            });
            await page.locator('.tree-item[data-kind="file"] .tree-row').first().click({ button: 'right' });
            const fileActions = await page.getByRole('menuitem').allTextContents();
            check(`${engine} file context menu excludes create actions`,
                JSON.stringify(fileActions) === JSON.stringify(['Move', 'Rename', 'Delete']),
                JSON.stringify(fileActions));
            await page.locator('.explorer-context-scrim').click({ position: { x: 4, y: 4 } });
            check(`${engine} left-click outside closes the context menu`, await page.getByRole('menu').count() === 0);
            await page.locator('.tree-item[data-kind="directory"]:not([data-root]) .tree-row').first().click({ button: 'right' });
            const directoryActions = await page.getByRole('menuitem').allTextContents();
            check(`${engine} folder context menu includes create and item actions`,
                JSON.stringify(directoryActions) === JSON.stringify(['New file', 'New folder', 'Move', 'Rename', 'Delete']),
                JSON.stringify(directoryActions));
            await compare(`${engine}-explorer-context`, await page.screenshot({ animations: 'disabled' }));
            await page.locator('.explorer-context-scrim').click({ button: 'right', position: { x: 4, y: 4 } });
            check(`${engine} right-click outside closes the context menu`, await page.getByRole('menu').count() === 0);
            await page.locator('.tree-item.selected .tree-row').focus();
            const openLayout = await page.evaluate(() => ({
                workspace: document.querySelector('.editor-workspace').getBoundingClientRect().width,
                panel: document.querySelector('.bottom-panel').getBoundingClientRect().width,
            }));
            await page.keyboard.press('Control+b');
            await page.waitForFunction(() => document.querySelector('.explorer').hidden);
            const closedLayout = await page.evaluate(() => ({
                workspace: document.querySelector('.editor-workspace').getBoundingClientRect().width,
                panel: document.querySelector('.bottom-panel').getBoundingClientRect().width,
            }));
            await page.keyboard.press('Control+b');
            await page.waitForFunction(() => !document.querySelector('.explorer').hidden);
            check(`${engine} opening Explorer resizes the editor and bottom panel together`,
                closedLayout.workspace > openLayout.workspace
                    && openLayout.panel === openLayout.workspace
                    && closedLayout.panel === closedLayout.workspace,
                JSON.stringify({ openLayout, closedLayout }));
            check(`${engine} keyboard collapse restores Explorer focus`,
                await page.locator('.tree-item.selected .tree-row').evaluate(element => element === document.activeElement));
            const resizer = await page.locator('.explorer-resizer').boundingBox();
            await page.mouse.move(resizer.x + resizer.width / 2, resizer.y + 80);
            await page.mouse.down();
            await page.mouse.move(resizer.x + 90, resizer.y + 80, { steps: 8 });
            await page.mouse.up();
            await page.waitForFunction(() => globalThis.resizeCommits.length === 1);
            const resizeResult = await page.evaluate(() => ({
                commits: globalThis.resizeCommits,
                width: Math.round(document.querySelector('.explorer').getBoundingClientRect().width),
                aria: Number(document.querySelector('.explorer-resizer').getAttribute('aria-valuenow')),
            }));
            check(`${engine} pointer resize is frame-coalesced and committed once`,
                resizeResult.commits.length === 1 && resizeResult.width === resizeResult.aria && resizeResult.width < 280,
                JSON.stringify(resizeResult));
            await page.evaluate(() => mountEditorGroupsFixture());
            const groupSplitter = await page.locator('#editor-groups-fixture .editor-splitter').boundingBox();
            await page.mouse.move(groupSplitter.x + groupSplitter.width / 2, groupSplitter.y + 40);
            await page.mouse.down();
            await page.mouse.move(groupSplitter.x + 70, groupSplitter.y + 40, { steps: 8 });
            await page.mouse.up();
            await page.waitForFunction(() => globalThis.editorGroupCommits.length === 1);
            const groupResize = await page.evaluate(() => ({
                commits: globalThis.editorGroupCommits,
                ratio: parseFloat(document.querySelector('#editor-groups-fixture .editor-split').style
                    .getPropertyValue('--split-first')),
            }));
            check(`${engine} editor splitter pointer resize commits once`,
                groupResize.commits.length === 1 && groupResize.ratio > 50,
                JSON.stringify(groupResize));
            await page.locator('#editor-groups-fixture .editor-splitter').focus();
            const beforeKeyboardResize = await page.locator('#editor-groups-fixture .editor-splitter').getAttribute('aria-valuenow');
            await page.keyboard.press('ArrowRight');
            const afterKeyboardResize = await page.locator('#editor-groups-fixture .editor-splitter').getAttribute('aria-valuenow');
            check(`${engine} editor splitter is keyboard adjustable`,
                Number(afterKeyboardResize) === Number(beforeKeyboardResize) + 5,
                `${beforeKeyboardResize} -> ${afterKeyboardResize}`);
            await page.dragAndDrop('#editor-groups-fixture .document-tab', '#editor-groups-fixture .group-drop-zone.left', { force: true });
            await page.dragAndDrop('#editor-groups-fixture .document-tab', '#editor-groups-fixture .tabs-strip', { force: true });
            const groupDrops = await page.evaluate(() => globalThis.editorGroupDrops);
            check(`${engine} editor edge and center drop targets are operable`,
                groupDrops.includes('left') && groupDrops.includes('center'), JSON.stringify(groupDrops));
            await page.evaluate(() => {
                const fixture = document.getElementById('editor-groups-fixture');
                NovaEditorGroups.detachSplitter(fixture.querySelector('.editor-splitter'));
                NovaEditorGroups.detachDragSurface(fixture);
                fixture.remove();
            });
            const focusRestored = await page.evaluate(() => {
                const selected = document.querySelector('.tree-item.selected .tree-row');
                const explorer = document.querySelector('.explorer');
                selected.focus();
                NovaWorkspace.rememberFocus();
                explorer.hidden = true;
                document.querySelector('.activity-item').focus();
                explorer.hidden = false;
                NovaWorkspace.restoreFocus();
                return document.activeElement === selected;
            });
            check(`${engine} hiding and restoring Explorer preserves its focus`, focusRestored);

            let heapBefore;
            let cdp;
            if (engine === 'chromium') {
                cdp = await context.newCDPSession(page);
                await cdp.send('HeapProfiler.collectGarbage');
                heapBefore = (await cdp.send('Runtime.getHeapUsage')).usedSize;
            }
            const cycleMetrics = await page.evaluate(async () => {
                const explorer = document.querySelector('.explorer');
                const panel = document.querySelector('.bottom-panel');
                const longTasks = [];
                const observer = PerformanceObserver.supportedEntryTypes.includes('longtask')
                    ? new PerformanceObserver(entries => longTasks.push(...entries.getEntries().map(entry => entry.duration)))
                    : null;
                observer?.observe({ type: 'longtask' });
                for (let index = 0; index < 100; index++) {
                    explorer.hidden = !explorer.hidden;
                    explorer.style.width = `${160 + index % 361}px`;
                    panel.hidden = !panel.hidden;
                    await new Promise(resolve => requestAnimationFrame(resolve));
                }
                observer?.disconnect();
                explorer.hidden = false;
                panel.hidden = true;
                return { longestTask: Math.max(0, ...longTasks), cycles: 100 };
            });
            check(`${engine} 100 shell cycles have no task over 50 ms`, cycleMetrics.longestTask <= 50, `${cycleMetrics.longestTask.toFixed(2)} ms`);
            if (cdp) {
                await cdp.send('HeapProfiler.collectGarbage');
                const heapAfter = (await cdp.send('Runtime.getHeapUsage')).usedSize;
                check('chromium 100 shell cycles retain no more than 10% heap', heapAfter <= heapBefore * 1.10, `${heapBefore} -> ${heapAfter}`);
            }
            await context.close();
        } finally { await browser.close(); }
    }
} finally { server.close(); }

const failed = results.filter(result => !result.ok);
process.stdout.write(`\n${results.length - failed.length} shell gates passed, ${failed.length} failed\n`);
if (process.env.NOVASHARP_BROWSER_METRICS) {
    const requested = path.parse(path.resolve(process.env.NOVASHARP_BROWSER_METRICS));
    const metricsPath = path.join(requested.dir, `${requested.name}-shell${requested.ext || '.json'}`);
    await mkdir(requested.dir, { recursive: true });
    await writeFile(metricsPath, `${JSON.stringify({
        fixtureName: process.env.NOVASHARP_FIXTURE_NAME ?? 'unrecorded',
        platform: process.platform,
        architecture: process.arch,
        nodeVersion: process.version,
        engineVersions,
        assertions: results,
    }, null, 2)}\n`);
}
process.exit(failed.length === 0 ? 0 : 1);
