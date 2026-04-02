export type ManifestEntry = {
  key: string;
  type: string;   // 'config' | 'roles' | 'connectors' | 'workflow' | 'script' | 'chat-history'
  name?: string;
  updatedAt?: string;
};

export type ProviderInfo = {
  provider_slug: string;
  provider_name: string;
  status: string;
  proxy_url?: string;
};
