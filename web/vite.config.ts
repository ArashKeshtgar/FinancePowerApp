import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API runs on :5400 (src/FinancePowerApp.Api). Proxying keeps the browser on one origin, so the API needs no CORS policy.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5401,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://localhost:5400', changeOrigin: false },
    },
  },
})
