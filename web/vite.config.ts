import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

function readVersion(): string {
  const v = readFileSync(fileURLToPath(new URL('../VERSION', import.meta.url)), 'utf8').trim();
  if (!/^\d+\.\d+\.\d+$/.test(v)) throw new Error(`VERSION file must contain MAJOR.MINOR.PATCH, got "${v}"`);
  return v;
}

export default defineConfig({
  plugins: [svelte()],
  define: {
    __APP_VERSION__: JSON.stringify(readVersion())
  },
  base: '/',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
    assetsInlineLimit: 0
  },
  worker: {
    format: 'es'
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8080',
        changeOrigin: false
      },
      '/healthz': {
        target: 'http://localhost:8080',
        changeOrigin: false
      },
      '/ws': {
        target: 'ws://localhost:8080',
        ws: true,
        changeOrigin: false
      }
    }
  }
});
