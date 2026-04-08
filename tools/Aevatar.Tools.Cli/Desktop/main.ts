import { app, BrowserWindow, ipcMain, shell } from 'electron';
import * as http from 'http';
import * as path from 'path';

const isDev = process.argv.includes('--dev');
const DEV_SERVER_URL = process.env.VITE_DEV_URL || 'http://localhost:5173';
const API_URL = process.env.AEVATAR_API_URL || 'https://aevatar-console-backend-api.aevatar.ai';

let mainWindow: BrowserWindow | null = null;
let authServer: http.Server | null = null;
let authRedirectUri: string | null = null;

function getFrontendPath(): string {
  if (isDev) {
    return path.join(__dirname, '..', '..', 'Frontend', 'dist');
  }
  return path.join(process.resourcesPath, 'frontend');
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: '#1A1A1A',
    titleBarStyle: process.platform === 'darwin' ? 'hiddenInset' : 'default',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
    show: false,
  });

  mainWindow.once('ready-to-show', () => {
    mainWindow?.show();
  });

  // Open external links in system browser
  mainWindow.webContents.setWindowOpenHandler(({ url: targetUrl }) => {
    shell.openExternal(targetUrl);
    return { action: 'deny' };
  });

  if (isDev) {
    mainWindow.loadURL(DEV_SERVER_URL);
    mainWindow.webContents.openDevTools({ mode: 'bottom' });
  } else {
    const frontendDir = getFrontendPath();
    mainWindow.loadFile(path.join(frontendDir, 'index.html'));
  }

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

// ── OAuth loopback server ──
// Starts a temporary HTTP server on a random port to receive the OAuth callback.
// The redirect URI is http://localhost:<port>/auth/callback.

function startAuthServer(): Promise<string> {
  return new Promise((resolve, reject) => {
    if (authServer) {
      if (authRedirectUri) return resolve(authRedirectUri);
      authServer.close();
    }

    const server = http.createServer((req, res) => {
      const reqUrl = new URL(req.url || '/', `http://localhost`);
      if (reqUrl.pathname !== '/auth/callback') {
        res.writeHead(404);
        res.end('Not found');
        return;
      }

      const code = reqUrl.searchParams.get('code');
      const state = reqUrl.searchParams.get('state');
      const error = reqUrl.searchParams.get('error');
      const errorDescription = reqUrl.searchParams.get('error_description');

      // Send a nice HTML page to the browser
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(`<!DOCTYPE html>
<html><head><title>Aevatar</title>
<style>body{font-family:system-ui;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;background:#1A1A1A;color:#fff;}
.card{text-align:center;padding:2rem;border-radius:12px;background:#2A2A2A;}
</style></head>
<body><div class="card">
<h2>${error ? 'Sign in failed' : 'Sign in successful'}</h2>
<p>${error ? (errorDescription || error) : 'You can close this tab and return to Aevatar.'}</p>
</div></body></html>`);

      // Forward to Electron renderer
      if (mainWindow) {
        mainWindow.webContents.send('auth-callback', {
          code,
          state,
          error,
          errorDescription,
        });
        if (mainWindow.isMinimized()) mainWindow.restore();
        mainWindow.focus();
      }

      // Shut down the temporary server
      setTimeout(() => {
        server.close();
        authServer = null;
        authRedirectUri = null;
      }, 1000);
    });

    server.listen(0, '127.0.0.1', () => {
      const addr = server.address();
      if (!addr || typeof addr === 'string') {
        reject(new Error('Failed to start auth server'));
        return;
      }
      authServer = server;
      authRedirectUri = `http://127.0.0.1:${addr.port}/auth/callback`;
      resolve(authRedirectUri);
    });

    server.on('error', reject);
  });
}

// Single instance lock
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(() => {
    createWindow();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        createWindow();
      }
    });
  });
}

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

// ── IPC handlers ──
ipcMain.handle('get-api-url', () => API_URL);
ipcMain.handle('get-auth-redirect-uri', async () => {
  return startAuthServer();
});
