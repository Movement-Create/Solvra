import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Builds into ../media/v3 with fixed file names so the extension can reference them.
// Everything (fonts, icons) is bundled locally: webview CSP blocks remote resources.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../media/v3',
    emptyOutDir: true,
    target: 'es2022',
    cssCodeSplit: false,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 4000,
    rollupOptions: {
      input: 'src/main.tsx',
      output: {
        entryFileNames: 'main.js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: (info) => (info.names?.some((n) => n.endsWith('.css')) ? 'main.css' : 'assets/[name]-[hash][extname]'),
      },
    },
  },
  server: { fs: { allow: ['..'] } },
  test: { environment: 'jsdom', include: ['src/**/*.test.ts', 'src/**/*.test.tsx'] },
});
