import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const isElectronBuild = process.env.ELECTRON_BUILD === '1';
const apiProxyTarget = process.env.AEVATAR_API_URL || '';

if (apiProxyTarget) {
  console.log(`[vite] API proxy: /api → ${apiProxyTarget}`);
}

export default defineConfig({
  plugins: [react()],
  base: isElectronBuild ? './' : '/',
  server: {
    ...(apiProxyTarget
      ? {
          proxy: {
            '/api': {
              target: apiProxyTarget,
              changeOrigin: true,
              secure: false,
              cookieDomainRewrite: { '*': '' },
              configure: (proxy) => {
                proxy.on('proxyReq', (proxyReq) => {
                  proxyReq.removeHeader('origin');
                  proxyReq.removeHeader('referer');
                });
                proxy.on('proxyRes', (proxyRes) => {
                  const setCookie = proxyRes.headers['set-cookie'];
                  if (setCookie) {
                    proxyRes.headers['set-cookie'] = setCookie.map((cookie: string) =>
                      cookie
                        .replace(/;\s*Secure/gi, '')
                        .replace(/;\s*SameSite=\w+/gi, '; SameSite=Lax'),
                    );
                  }
                });
              },
            },
          },
        }
      : {}),
  },
  build: {
    outDir: isElectronBuild ? 'dist' : '../wwwroot',
    emptyOutDir: true,
    assetsDir: isElectronBuild ? 'assets' : '',
    cssCodeSplit: false,
    rollupOptions: {
      output: {
        inlineDynamicImports: true,
        entryFileNames: isElectronBuild ? 'assets/app.js' : 'app.js',
        assetFileNames: assetInfo => {
          const assetName = assetInfo.name || '';
          if (isElectronBuild) {
            return assetName.slice(-4) === '.css' ? 'assets/app.css' : 'assets/[name][extname]';
          }
          return assetName.slice(-4) === '.css' ? 'app.css' : '[name][extname]';
        },
      },
    },
  },
});
