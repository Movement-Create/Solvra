import type { WebviewMessage } from '../../src/protocol';

interface VsCodeApi { postMessage(msg: unknown): void; getState(): unknown; setState(s: unknown): void }
declare global { function acquireVsCodeApi(): VsCodeApi }

// Outside VS Code (the screenshot harness, `npm run dev`) messages go to window.__solvraHost if present.
const api: VsCodeApi = typeof acquireVsCodeApi === 'function'
  ? acquireVsCodeApi()
  : {
      postMessage: (m) => (window as unknown as { __solvraHost?: (m: unknown) => void }).__solvraHost?.(m),
      getState: () => JSON.parse(sessionStorage.getItem('solvra-state') || 'null'),
      setState: (s) => sessionStorage.setItem('solvra-state', JSON.stringify(s)),
    };

export const post = (msg: WebviewMessage) => api.postMessage(msg);
export const getPersisted = <T,>(): Partial<T> => (api.getState() as Partial<T>) ?? {};
export const persist = (s: unknown) => api.setState(s);
