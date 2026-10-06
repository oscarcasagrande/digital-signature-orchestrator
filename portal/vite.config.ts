/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the API runs on :8080; in the compose network nginx proxies /v1 instead.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/v1': { target: process.env.VITE_API_TARGET ?? 'http://localhost:8080', changeOrigin: true },
      '/realms': { target: process.env.VITE_AUTH_TARGET ?? 'http://localhost:8180', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/__tests__/setup.ts'],
    css: false,
  },
});
