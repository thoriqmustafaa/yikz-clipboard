import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

const version = readFileSync(fileURLToPath(new URL('../VERSION', import.meta.url)), 'utf8').trim();

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(version)
  },
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
    testTimeout: 60000
  }
});
