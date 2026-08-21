import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { VITE_BASE } from './src/config/basePath';

/** Where the API listens during local development (outside Compose). */
const DEV_API_TARGET = process.env.DEV_API_TARGET ?? 'http://localhost:8080';

export default defineConfig({
  // Derived from the same constant as the router basename (design D1).
  base: VITE_BASE,
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      // Lets the app call `/api/...` by relative path in dev exactly as it does through
      // Caddy in the Compose stack (design D3) — no environment-specific API base URL.
      '/api': {
        target: DEV_API_TARGET,
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
