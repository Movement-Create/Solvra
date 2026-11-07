# Solvra on Windows

`scripts/windows/build-bundle.sh <vsix> [out-dir]` builds `solvra-windows-<ver>.zip` from a packaged
VSIX (or run `scripts/build-release.sh` for every release file). The zip holds the backend (taken
from the VSIX), the VSIX, `install.ps1`, `Install Solvra.cmd`, `README.txt`, `VERSION` and
`SHA256SUMS`.

On Windows 10/11, extract the zip and double-click **Install Solvra.cmd** (or run
`.\install.ps1` in PowerShell). Everything is per user, with no administrator rights:

- **.NET 8**: uses an installed runtime, else installs one to `%LOCALAPPDATA%\Microsoft\dotnet`
  with Microsoft's `dotnet-install.ps1`.
- **Backend**: `%LOCALAPPDATA%\Solvra\releases\<version>`, with the junction `current` pointing at it.
  The `solvra` command is `%LOCALAPPDATA%\Solvra\bin\solvra.cmd`, added to your user PATH.
- **Git for Windows** (via winget) when missing: Solvra runs commands with its bash. Skip it with `-NoGit`.
- **ChatGPT**: installs Node.js (winget) and the Codex CLI (`npm install -g @openai/codex`) when
  missing, runs `codex login` (browser), and makes `chatgpt:<model>` the default. Skip it with
  `-NoChatGPT`; `-Model gpt-5.6-luna` picks another model.
- **VS Code**: installs the extension with `code --install-extension`. It also sets
  `solvra.executablePath` (the DLL, never a `.cmd`), `solvra.dotnetPath` and `solvra.model` in
  `%APPDATA%\Code\User\settings.json`, keeping a backup. A settings file with comments is left
  alone, and the settings are printed instead.
- **Check**: sends one tiny test request.

Updating: run the newer zip's installer; it replaces the release and keeps settings. Removing:
`code --uninstall-extension solvra.solvra-vscode`, delete `%LOCALAPPDATA%\Solvra`, and remove its
`bin` folder from your user PATH.

`pwsh scripts/windows/test-install.ps1 scripts/windows/install.ps1` checks the installer's
helpers (wrapper text and settings merge) on any OS.
