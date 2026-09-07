import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    host: '0.0.0.0', allowedHosts: true,
    proxy: { '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true }, '/health': { target: 'http://127.0.0.1:5080' } },
  },
})
