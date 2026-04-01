import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@nyxids/oauth-core': path.resolve(__dirname, '../../../../NyxID/sdk/oauth-core/src/index.ts'),
      '@nyxids/oauth-react': path.resolve(__dirname, '../../../../NyxID/sdk/oauth-react/src/index.tsx'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/nyxid': {
        target: 'https://auth.nyxid.io',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/nyxid/, ''),
      },
      '/proxy': {
        target: 'https://proxy.nyxid.io',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/proxy/, ''),
      },
      '/aevatar-api': {
        target: 'https://mainnet.aevatar.ai',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/aevatar-api/, ''),
      },
    },
  },
})
