# Solvra on macOS

`build-bundle.sh` (run on a build host with the .NET 8 SDK) turns a packaged VSIX into
`dist/solvra-macos-<ver>.tar.gz`. It contains the VSIX, the backend copied out of that VSIX,
`install.sh`, `VERSION` and `SHA256SUMS`. The backend is a framework-dependent .NET 8 build
that includes the `osx-arm64` and `osx-x64` native libraries, so a single bundle covers
Apple Silicon and Intel Macs.

```sh
scripts/macos/build-bundle.sh vscode-extension/solvra-vscode-0.2.0.vsix
```

On the Mac:

```sh
tar -xzf solvra-macos-0.2.0.tar.gz && cd solvra-macos-0.2.0
shasum -a 256 -c SHA256SUMS      # optional integrity check
./install.sh                     # --no-vscode | --no-cli | --no-dotnet | --prefix DIR
```

`install.sh` installs everything per user, with no sudo:

- Finds a .NET 8 runtime (official `.pkg`, Homebrew `dotnet@8`, `~/.dotnet`, or `PATH`).
  If none is found, it installs one into `~/.dotnet` using Microsoft's `dotnet-install.sh`.
- Installs the backend to `~/.local/opt/solvra/releases/<ver>` and points `current` at it.
  The extension also checks this location.
- Writes the `~/.local/bin/solvra` wrapper and adds a marked PATH block to
  `~/.zprofile` (or `~/.bash_profile` for bash users).
- Installs the extension with `code --install-extension`. It finds the `code` CLI inside
  `/Applications/Visual Studio Code.app`, even if the shell command was never installed.
- Removes the `com.apple.quarantine` attribute from the bundle so Gatekeeper does not block
  the bundled `libQuestPdfSkia.dylib`.

Re-running the script is safe; it replaces the release and does not add the PATH block twice.
To uninstall: `code --uninstall-extension solvra.solvra-vscode`, then
`rm -rf ~/.local/opt/solvra ~/.local/bin/solvra`, then delete the `solvra installer` block
from your shell profile.

## Provider: OpenAI-compatible (same as the devbox)

```sh
./configure-openai.sh                 # provider openai, OpenCode Zen Go gateway, model glm-5.3
./configure-openai.sh --reset-key     # replace an expired key
./configure-openai.sh --base-url https://api.openai.com/v1 --model gpt-4.1   # plain OpenAI
```

- Prompts once for the API key and stores it in the login Keychain (service `solvra-openai`).
- Writes non-secret settings to `~/.config/solvra/env.sh`. The `~/.local/bin/solvra` wrapper
  loads that file and reads the key from the Keychain on each launch. OpenAI variables are not
  added to your global shell, so other OpenAI tools are unaffected.
- Sets VS Code's `solvra.executablePath` to the wrapper, plus `solvra.provider` and
  `solvra.model`, so the extension and the CLI use identical settings. If `settings.json`
  contains comments, it prints the three settings for you to paste instead of editing the file.
- Sends one tiny test request and reports errors such as an expired key.

## Provider: ChatGPT subscription via Codex sign-in (no API key)

```sh
./configure-codex.sh                  # chatgpt provider, model gpt-5.6-sol
./configure-codex.sh --model gpt-5.6-luna
```

This requires the Codex CLI (`brew install codex` or `npm install -g @openai/codex`). If Codex is
not signed in yet, the script runs `codex login`. Solvra only reads `~/.codex/auth.json`;
refreshing the login is left to `codex app-server`. The script writes `SOLVRA_PROVIDER=chatgpt`,
the model and `SOLVRA_CODEX_BIN` to `~/.config/solvra/env.sh`, points VS Code at the wrapper,
and sends a one-line test request. Both configure scripts update VS Code settings through the
shared `vscode-settings.sh`; running either one switches the provider.
