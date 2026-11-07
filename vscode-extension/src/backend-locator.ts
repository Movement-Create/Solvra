// Finds the Solvra backend and the .NET runtime that runs it, on macOS, Linux and Windows.
// No `vscode` import: everything comes in through LocatorInput (unit-tested).
import * as path from 'path';

export interface Invocation {
  /** Program to start: dotnet, or a native wrapper such as ~/.local/bin/solvra. */
  command: string;
  /** Arguments before Solvra's own (the DLL when started through dotnet). */
  prefix: string[];
  /** Where it was found, for logs and errors. */
  source: string;
}

export interface LocatorInput {
  platform: NodeJS.Platform;
  home: string;
  env: NodeJS.ProcessEnv;
  exists: (p: string) => boolean;
  isDirectory: (p: string) => boolean;
  /** solvra.executablePath: Solvra.dll, a solvra wrapper, or a folder containing Solvra.dll. */
  executablePath?: string;
  /** solvra.solvraPath (older setting): a Solvra repository or release folder. */
  solvraPath?: string;
  /** solvra.dotnetPath; empty or "dotnet" means find it. */
  dotnetPath?: string;
  extensionPath: string;
  workspaceFolders: string[];
}

export interface LocatorResult { invocation?: Invocation; searched: string[]; error?: string }

/** Solvra.dll inside a release folder or a repository checkout. */
export function dllIn(dir: string, exists: (p: string) => boolean): string | undefined {
  const candidates = [
    path.join(dir, 'Solvra.dll'),
    path.join(dir, 'backend', 'Solvra.dll'),
    ...['Release', 'Debug'].flatMap((c) => ['net8.0', 'net9.0'].map((tf) => path.join(dir, 'src', 'Solvra', 'bin', c, tf, 'Solvra.dll'))),
  ];
  return candidates.find(exists);
}

export function installedDir(i: Pick<LocatorInput, 'platform' | 'home' | 'env'>): string {
  return i.platform === 'win32'
    ? path.join(i.env.LOCALAPPDATA || path.join(i.home, 'AppData', 'Local'), 'Solvra', 'current')
    : path.join(i.home, '.local', 'opt', 'solvra', 'current');
}

/** The dotnet executable: the setting, DOTNET_ROOT, PATH, then the usual install locations. */
export function findDotnet(i: LocatorInput): string {
  const setting = (i.dotnetPath ?? '').trim();
  if (setting && setting !== 'dotnet') return setting;
  const exe = i.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const sep = i.platform === 'win32' ? ';' : ':';
  const candidates: string[] = [];
  if (i.env.DOTNET_ROOT) candidates.push(path.join(i.env.DOTNET_ROOT, exe));
  for (const dir of (i.env.PATH || i.env.Path || '').split(sep)) if (dir) candidates.push(path.join(dir, exe));
  if (i.platform === 'win32') {
    for (const root of [i.env.ProgramFiles, i.env.ProgramW6432].filter(Boolean) as string[]) candidates.push(path.join(root, 'dotnet', exe));
    if (i.env.LOCALAPPDATA) candidates.push(path.join(i.env.LOCALAPPDATA, 'Microsoft', 'dotnet', exe));
  } else if (i.platform === 'darwin') {
    candidates.push('/usr/local/share/dotnet/dotnet', '/opt/homebrew/opt/dotnet@8/libexec/dotnet', '/usr/local/opt/dotnet@8/libexec/dotnet', '/opt/homebrew/bin/dotnet');
  } else {
    candidates.push('/usr/share/dotnet/dotnet', '/usr/lib/dotnet/dotnet', '/usr/bin/dotnet', '/snap/bin/dotnet');
  }
  candidates.push(path.join(i.home, '.dotnet', exe));
  return candidates.find(i.exists) ?? 'dotnet';
}

export function locateBackend(i: LocatorInput): LocatorResult {
  const searched: string[] = [];
  const viaDotnet = (dll: string, source: string): LocatorResult => ({ invocation: { command: findDotnet(i), prefix: [dll], source }, searched });

  // 1. solvra.executablePath
  const exe = (i.executablePath ?? '').trim();
  if (exe) {
    if (i.isDirectory(exe)) {
      const dll = dllIn(exe, i.exists);
      if (dll) return viaDotnet(dll, 'solvra.executablePath');
      return { searched, error: `solvra.executablePath points to ${exe}, which has no Solvra.dll.` };
    }
    if (!i.exists(exe)) return { searched, error: `solvra.executablePath points to ${exe}, which does not exist.` };
    if (/\.dll$/i.test(exe)) return viaDotnet(exe, 'solvra.executablePath');
    if (i.platform === 'win32' && /\.(cmd|bat)$/i.test(exe)) {
      // Never start .cmd files: the prompt would pass through cmd.exe. Use the DLL next to it instead.
      const dll = dllIn(path.join(path.dirname(exe), '..', 'current'), i.exists) ?? dllIn(path.dirname(exe), i.exists);
      if (dll) return viaDotnet(dll, 'solvra.executablePath (next to the .cmd)');
      return { searched, error: `solvra.executablePath is a .cmd file (${exe}). Point it at Solvra.dll instead.` };
    }
    return { invocation: { command: exe, prefix: [], source: 'solvra.executablePath' }, searched };
  }
  searched.push('solvra.executablePath (not set)');

  // 2. solvra.solvraPath (older setting)
  const repo = (i.solvraPath ?? '').trim();
  if (repo) {
    const dll = dllIn(repo, i.exists);
    if (dll) return viaDotnet(dll, 'solvra.solvraPath');
    searched.push(`solvra.solvraPath: ${repo} (no Solvra.dll)`);
  }

  // 3. The backend shipped inside the extension.
  const bundled = path.join(i.extensionPath, 'backend', 'Solvra.dll');
  if (i.exists(bundled)) return viaDotnet(bundled, 'bundled with the extension');
  searched.push(`${bundled} (not bundled)`);

  // 4. The installer's location.
  const installed = path.join(installedDir(i), 'Solvra.dll');
  if (i.exists(installed)) return viaDotnet(installed, 'installed');
  searched.push(`${installed} (not installed)`);

  // 5. Development: an open Solvra checkout, or the repository around the extension.
  for (const folder of [...i.workspaceFolders, path.resolve(i.extensionPath, '..')]) {
    const dll = dllIn(folder, i.exists);
    if (dll) return viaDotnet(dll, `build in ${folder}`);
    searched.push(`${folder} (no build)`);
  }
  return { searched, error: 'Solvra backend not found.' };
}
