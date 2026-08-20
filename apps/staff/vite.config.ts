import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { VITE_BASE } from './src/config/basePath';

/** Where the API listens during local development (outside Compose). */
const DEV_API_TARGET = process.env.DEV_API_TARGET ?? 'http://localhost:8080';

export default defineConfig({
  // '/staff/' — derived from the same constant as the router basename (design D1).
  // This is what makes emitted asset URLs absolute under /staff/.
  base: VITE_BASE,
  plugins: [react(), tailwindcss()],
  server: {
    // A different port from the patient portal so both dev servers can run at once.
    port: 5174,
    proxy: {
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
