import { defineConfig } from 'vitest/config'

// Separate from vite.config.ts so the test runner can use a DOM environment
// (localStorage, document.dir) without affecting the production build config.
export default defineConfig({
  test: {
    environment: 'jsdom',
  },
})
