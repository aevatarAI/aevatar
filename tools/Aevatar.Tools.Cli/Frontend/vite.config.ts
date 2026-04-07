import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    assetsDir: '',
    rollupOptions: {
      output: {
        inlineDynamicImports: true,
        entryFileNames: 'app-[hash].js',
        assetFileNames: assetInfo => {
          const assetName = assetInfo.name || '';
          return assetName.slice(-4) === '.css' ? 'app-[hash][extname]' : '[name]-[hash][extname]';
        },
      },
    },
  },
});
