// Approximate VS Code theme variables (Light+, Dark+, High Contrast) for screenshots outside VS Code.
export const THEMES = {
  light: { cls: 'vscode-light', vars: {
    '--vscode-foreground': '#3b3b3b', '--vscode-sideBar-background': '#f8f8f8', '--vscode-editor-background': '#ffffff',
    '--vscode-descriptionForeground': '#717171', '--vscode-input-background': '#ffffff', '--vscode-input-foreground': '#3b3b3b',
    '--vscode-widget-border': '#e5e5e5', '--vscode-focusBorder': '#005fb8', '--vscode-textLink-foreground': '#005fb8',
    '--vscode-list-hoverBackground': '#f2f2f2', '--vscode-list-activeSelectionBackground': '#e8e8e8', '--vscode-list-activeSelectionForeground': '#000000',
    '--vscode-menu-background': '#ffffff', '--vscode-menu-foreground': '#3b3b3b', '--vscode-textCodeBlock-background': '#f5f5f5',
    '--vscode-errorForeground': '#c72e0f', '--vscode-button-background': '#005fb8', '--vscode-button-foreground': '#ffffff',
  } },
  dark: { cls: 'vscode-dark', vars: {
    '--vscode-foreground': '#cccccc', '--vscode-sideBar-background': '#181818', '--vscode-editor-background': '#1f1f1f',
    '--vscode-descriptionForeground': '#9d9d9d', '--vscode-input-background': '#313131', '--vscode-input-foreground': '#cccccc',
    '--vscode-widget-border': '#313131', '--vscode-focusBorder': '#0078d4', '--vscode-textLink-foreground': '#4daafc',
    '--vscode-list-hoverBackground': '#2a2d2e', '--vscode-list-activeSelectionBackground': '#04395e', '--vscode-list-activeSelectionForeground': '#ffffff',
    '--vscode-menu-background': '#1f1f1f', '--vscode-menu-foreground': '#cccccc', '--vscode-textCodeBlock-background': '#2b2b2b',
    '--vscode-errorForeground': '#f85149', '--vscode-button-background': '#0078d4', '--vscode-button-foreground': '#ffffff',
  } },
  hc: { cls: 'vscode-high-contrast', vars: {
    '--vscode-foreground': '#ffffff', '--vscode-sideBar-background': '#000000', '--vscode-editor-background': '#000000',
    '--vscode-descriptionForeground': '#ffffff', '--vscode-input-background': '#000000', '--vscode-input-foreground': '#ffffff',
    '--vscode-widget-border': '#6fc3df', '--vscode-contrastBorder': '#6fc3df', '--vscode-focusBorder': '#f38518', '--vscode-textLink-foreground': '#21a6ff',
    '--vscode-list-hoverBackground': 'transparent', '--vscode-menu-background': '#000000', '--vscode-menu-foreground': '#ffffff',
    '--vscode-textCodeBlock-background': '#000000', '--vscode-errorForeground': '#f48771', '--vscode-button-background': '#000000', '--vscode-button-foreground': '#ffffff',
  } },
};
