import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Curato.Api serves the built frontend directly in production (PLAN.md
    // §1: one deployable artifact); this proxy is dev-only so `npm run dev`
    // can talk to a locally running API without CORS setup.
    proxy: {
      '/api': 'http://localhost:5299',
      '/health': 'http://localhost:5299',
    },
  },
})
