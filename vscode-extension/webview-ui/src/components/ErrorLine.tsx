import type { FixKind } from '../../../src/protocol';
import { post } from '../vscode';
import { Icon } from './Icon';

const PROVIDER_LABEL: Record<string, string> = { anthropic: 'Anthropic', openai: 'OpenAI', google: 'Google', moonshot: 'Moonshot', brave: 'Brave Search' };

/** An error inside the chat with the button that fixes it. */
export function ErrorLine({ message, fix, provider, onDismiss, onOpenSettings }: {
  message: string; fix: FixKind | null; provider?: string | null; onDismiss?: () => void; onOpenSettings?: () => void;
}) {
  const openSettings = () => (onOpenSettings ? onOpenSettings() : window.dispatchEvent(new CustomEvent('solvra:view', { detail: 'settings' })));
  const button = (() => {
    switch (fix) {
      case 'add_api_key': return { label: provider && PROVIDER_LABEL[provider] ? `Add ${PROVIDER_LABEL[provider]} API key` : 'Add API key', run: openSettings };
      case 'set_executable': return { label: 'Set Solvra location', run: openSettings };
      case 'install_dotnet': return { label: 'Install .NET 8', run: () => post({ type: 'runFix', fix: 'install_dotnet' }) };
      case 'codex_login': return { label: 'Sign in with Codex', run: () => post({ type: 'runFix', fix: 'codex_login' }) };
      case 'retry': return { label: 'Retry', run: () => window.dispatchEvent(new CustomEvent('solvra:retry')) };
      default: return null;
    }
  })();
  return (
    <div className="error-line" role="alert">
      <Icon name="error" className="error-icon" />
      <div className="grow">
        <div className="error-text" dir="auto">{message}</div>
        {fix === 'rate_limited' && <div className="dim small">The provider is limiting requests. Wait a moment, or switch models from the model menu.</div>}
      </div>
      {button && <button className="btn primary small" onClick={button.run}>{button.label}</button>}
      {onDismiss && <button className="icon-btn small" aria-label="Dismiss" onClick={onDismiss}><Icon name="close" /></button>}
    </div>
  );
}
