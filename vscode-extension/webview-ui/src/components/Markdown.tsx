import { isValidElement, memo, type ReactNode } from 'react';
import { defaultComponents, Streamdown } from 'streamdown';
import { shikiPlugin } from '../highlight';
import { parseFileRef } from '../store';
import { post } from '../vscode';

const isExternal = (href: string) => /^(https?|mailto):/i.test(href);

/** Plain text of rendered children (streamed text arrives wrapped in animation spans). */
function textOf(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return '';
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(textOf).join('');
  if (isValidElement<{ children?: ReactNode }>(node)) return textOf(node.props.children);
  return '';
}

function FileLink({ path, line, children }: { path: string; line?: number; children: ReactNode }) {
  return (
    <button className="file-link" title={`Open ${path}${line ? ` at line ${line}` : ''}`} onClick={() => post({ type: 'openFile', path, line })}>
      {children}
    </button>
  );
}

/** Assistant text: plain, calm markdown. File references (`src/app.ts:12`) open in the editor. */
export const Markdown = memo(function Markdown({ text, streaming }: { text: string; streaming: boolean }) {
  return (
    <Streamdown
      mode={streaming ? 'streaming' : 'static'}
      isAnimating={streaming}
      parseIncompleteMarkdown={streaming}
      dir="auto"
      className="md"
      plugins={{ code: shikiPlugin }}
      controls={{ code: { copy: true, download: false }, table: false, mermaid: false }}
      linkSafety={{ enabled: false }}
      components={{
        code: (props) => {
          const { children, className } = props;
          const block = (props as { 'data-block'?: unknown })['data-block'] !== undefined || !!className;
          const text = block ? '' : textOf(children);
          if (text && !text.includes('\n')) {
            const ref = parseFileRef(text);
            if (ref) return <FileLink path={ref.path} line={ref.line}><code className="inline">{text}</code></FileLink>;
          }
          // Fenced blocks (and other inline code) keep Streamdown's renderer: highlighting, copy button.
          const Default = defaultComponents.code as React.ComponentType<typeof props>;
          return <Default {...props} />;
        },
        a: ({ href = '', children, node: _node, ...rest }) => {
          if (!isExternal(href)) {
            const ref = parseFileRef(decodeURIComponent(href.replace(/^file:\/\//, '').replace(/#L(\d+).*$/, ':$1')));
            if (ref) return <FileLink path={ref.path} line={ref.line}>{children}</FileLink>;
          }
          return <a href={href} {...rest}>{children}</a>;
        },
      }}
    >
      {text}
    </Streamdown>
  );
});
