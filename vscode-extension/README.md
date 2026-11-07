# Solvra for VS Code

Solvra is a coding agent that works inside a calm chat panel. It reads your project, edits files,
runs commands and delegates to sub-agents, and it asks before it changes anything unless you
choose Auto-approve.

## Getting started

The easiest way to get started is the installer for your system, from the
[latest release](https://github.com/Movement-Create/Solvra/releases/latest):

- **macOS:** download `solvra-macos-<version>.tar.gz`, then run
  `tar -xzf solvra-macos-*.tar.gz && cd solvra-macos-* && ./install.sh`.
- **Windows:** download `solvra-windows-<version>.zip`, extract it, and double-click
  **Install Solvra.cmd**.

The installer sets up .NET 8 (if needed), the `solvra` command, this extension, and ChatGPT
through your Codex sign-in, so no API key is needed. Then reload VS Code and click the Solvra icon
in the Activity Bar.

If you installed only this extension (the `.vsix`): it already includes the Solvra backend and
needs the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0). To use ChatGPT,
install the Codex CLI and sign in. The chat has a **Sign in with Codex** button that does both.
Other providers (Anthropic, OpenAI, Google, Moonshot, Ollama) work with an API key added under
**Settings** in the chat.

## Using it

- Type a message. Press `@` to add files or folders, and `/` for commands.
- **Ask before edits** (the default) shows each edit and command for approval: `1` Yes,
  `2` Yes and don't ask again, `3` No and say what to do instead. Press ⇧Tab to switch to
  **Plan only** or **Auto-approve**.
- Every step appears as one line; click it to see the diff or output. **Review** opens an edit in
  VS Code's diff editor, with **Keep** and **Undo** there and in the chat. **Restore files to before
  this message** rolls a conversation back.
- Sessions, model, keys, memory, tools and settings are all in the panel. The command palette is
  not needed.
- On Windows, running commands needs Git for Windows (it includes Git Bash). The installer offers
  to install it.

## Settings

Everything can be changed from the chat's **Settings** view. The underlying VS Code settings:
`solvra.model` (as `provider:model`, default `chatgpt:gpt-5.6-sol`), `solvra.effort`,
`solvra.maxTurns`, `solvra.maxBudget`, `solvra.autoApprove`, `solvra.executablePath` and
`solvra.dotnetPath`. API keys are kept in VS Code's secret storage, never in settings files.
