export type ConfigFile = 'config.json' | 'roles.json' | 'connectors.json' | `chat-history:${string}` | `workflow:${string}` | `script:${string}`;

export type WorkflowEntry = {
  workflowId: string;
  name: string;
  directoryLabel: string;
  stepCount: number;
  updatedAtUtc: string;
  description: string;
};

export type ScriptEntry = {
  scriptId: string;
  activeRevision: string;
  updatedAt: string;
  sourceText: string;
};

export type ProviderInfo = {
  provider_slug: string;
  provider_name: string;
  status: string;
  proxy_url?: string;
};
