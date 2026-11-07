// Line-delimited JSON parser for the backend's stream-json stdout. No `vscode` import (unit-tested).

export class NdjsonParser<T = unknown> {
  private buffer = '';

  constructor(
    private readonly onValue: (value: T) => void,
    /** Lines that are not JSON objects (should not happen; logged by the caller). */
    private readonly onOther: (line: string) => void = () => {},
  ) {}

  /** Feed a chunk of stdout. Complete lines are parsed at once; a partial line waits for the rest. */
  push(chunk: string): void {
    this.buffer += chunk;
    let nl: number;
    while ((nl = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, nl).replace(/\r$/, '');
      this.buffer = this.buffer.slice(nl + 1);
      this.line(line);
    }
  }

  /** Flush a trailing line without a newline (call when the stream ends). */
  end(): void {
    const rest = this.buffer;
    this.buffer = '';
    if (rest.trim()) this.line(rest);
  }

  private line(line: string): void {
    const trimmed = line.trim();
    if (!trimmed) return;
    if (!trimmed.startsWith('{')) { this.onOther(line); return; }
    let value: unknown;
    try { value = JSON.parse(trimmed); } catch { this.onOther(line); return; }
    if (value && typeof value === 'object' && typeof (value as { type?: unknown }).type === 'string') this.onValue(value as T);
    else this.onOther(line);
  }
}
