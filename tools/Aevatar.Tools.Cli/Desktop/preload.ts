import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('electronAPI', {
  /** Configured backend API base URL */
  getApiUrl: (): Promise<string> => ipcRenderer.invoke('get-api-url'),

  /** Start loopback auth server and return its redirect URI */
  getAuthRedirectUri: (): Promise<string> => ipcRenderer.invoke('get-auth-redirect-uri'),

  /** Current platform */
  platform: process.platform,

  /** Listen for OAuth callback from loopback server */
  onAuthCallback: (
    callback: (data: {
      code: string | null;
      state: string | null;
      error: string | null;
      errorDescription: string | null;
    }) => void,
  ): (() => void) => {
    const handler = (_event: Electron.IpcRendererEvent, data: any) => callback(data);
    ipcRenderer.on('auth-callback', handler);
    return () => ipcRenderer.removeListener('auth-callback', handler);
  },
});
