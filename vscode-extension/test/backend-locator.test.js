// Backend and dotnet lookup on macOS, Linux and Windows (compiled to out/ by `npm test`).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { locateBackend, findDotnet } = require('../out/backend-locator');

function input(files, overrides = {}) {
  const set = new Set(files);
  const dirs = new Set(overrides.dirs ?? []);
  return {
    platform: 'darwin', home: '/Users/a', env: { PATH: '/usr/bin' },
    exists: (p) => set.has(p), isDirectory: (p) => dirs.has(p),
    extensionPath: '/Users/a/.vscode/extensions/solvra.solvra-vscode-0.3.0', workspaceFolders: [],
    ...overrides,
  };
}

test('bundled backend is used when nothing is configured', () => {
  const dll = '/Users/a/.vscode/extensions/solvra.solvra-vscode-0.3.0/backend/Solvra.dll';
  const r = locateBackend(input([dll, '/usr/local/share/dotnet/dotnet']));
  assert.deepEqual(r.invocation, { command: '/usr/local/share/dotnet/dotnet', prefix: [dll], source: 'bundled with the extension' });
});

test('executablePath wins: a wrapper runs directly, a DLL runs through dotnet, a folder is searched', () => {
  const wrapper = '/Users/a/.local/bin/solvra';
  assert.deepEqual(locateBackend(input([wrapper], { executablePath: wrapper })).invocation, { command: wrapper, prefix: [], source: 'solvra.executablePath' });
  const dll = '/opt/s/Solvra.dll';
  assert.deepEqual(locateBackend(input([dll], { executablePath: dll, dotnetPath: '/x/dotnet' })).invocation.prefix, [dll]);
  assert.equal(locateBackend(input([dll], { executablePath: dll, dotnetPath: '/x/dotnet' })).invocation.command, '/x/dotnet');
  assert.equal(locateBackend(input([dll], { executablePath: '/opt/s', dirs: ['/opt/s'] })).invocation.prefix[0], dll);
  assert.match(locateBackend(input([], { executablePath: '/nope' })).error, /does not exist/);
});

test('installed location on macOS and Windows', () => {
  const mac = '/Users/a/.local/opt/solvra/current/Solvra.dll';
  assert.equal(locateBackend(input([mac])).invocation.prefix[0], mac);
  const local = 'C:\\Users\\a\\AppData\\Local';
  const win = path.join(local, 'Solvra', 'current', 'Solvra.dll');
  const r = locateBackend(input([win], { platform: 'win32', home: 'C:\\Users\\a', env: { LOCALAPPDATA: local } }));
  assert.equal(r.invocation.prefix[0], win);
});

test('Windows never starts a .cmd: it uses the DLL next to it', () => {
  const local = 'C:\\Users\\a\\AppData\\Local';
  const cmd = path.join(local, 'Solvra', 'bin', 'solvra.cmd');
  const dll = path.join(local, 'Solvra', 'bin', '..', 'current', 'Solvra.dll');
  const r = locateBackend(input([cmd, dll], { platform: 'win32', executablePath: cmd, env: { LOCALAPPDATA: local } }));
  assert.deepEqual(r.invocation.prefix, [dll]);
  assert.match(locateBackend(input([cmd], { platform: 'win32', executablePath: cmd, env: {} })).error, /Point it at Solvra.dll/);
});

test('dev checkout via solvraPath or an open workspace; error lists what was searched', () => {
  const build = '/w/Solvra/src/Solvra/bin/Debug/net8.0/Solvra.dll';
  assert.equal(locateBackend(input([build], { solvraPath: '/w/Solvra' })).invocation.prefix[0], build);
  assert.equal(locateBackend(input([build], { workspaceFolders: ['/w/Solvra'] })).invocation.source, 'build in /w/Solvra');
  const none = locateBackend(input([]));
  assert.equal(none.invocation, undefined);
  assert.ok(none.searched.some((s) => s.includes('not bundled')));
});

test('dotnet: setting, DOTNET_ROOT, PATH, usual folders, else bare name', () => {
  assert.equal(findDotnet(input([], { dotnetPath: '/my/dotnet' })), '/my/dotnet');
  assert.equal(findDotnet(input(['/r/dotnet'], { env: { DOTNET_ROOT: '/r' } })), '/r/dotnet');
  assert.equal(findDotnet(input(['/opt/homebrew/opt/dotnet@8/libexec/dotnet'])), '/opt/homebrew/opt/dotnet@8/libexec/dotnet');
  assert.equal(findDotnet(input(['/Users/a/.dotnet/dotnet'])), '/Users/a/.dotnet/dotnet');
  const pf = path.join('C:\\Program Files', 'dotnet', 'dotnet.exe');
  assert.equal(findDotnet(input([pf], { platform: 'win32', env: { ProgramFiles: 'C:\\Program Files' } })), pf);
  assert.equal(findDotnet(input([])), 'dotnet');
});
