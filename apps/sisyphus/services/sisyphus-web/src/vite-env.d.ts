/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_NYXID_BASE_URL: string
  readonly VITE_NYXID_CLIENT_ID: string
  readonly VITE_NYXID_PROXY_URL: string
  readonly VITE_AEVATAR_API_URL: string
  readonly VITE_DEFAULT_GRAPH_ID: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
