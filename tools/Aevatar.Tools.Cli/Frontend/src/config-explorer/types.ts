export type ConfigFile = 'config.json' | 'roles.json' | 'connectors.json' | 'actors.json' | `chat-history:${string}` | `workflow:${string}`;

export type WorkflowEntry = {
  workflowId: string;
  name: string;
  directoryLabel: string;
  stepCount: number;
  updatedAtUtc: string;
  description: string;
};

export type GAgentType = { typeName: string; fullName: string; assemblyName: string };
export type ActorGroup = { gAgentType: string; actorIds: string[] };

export type ProviderInfo = {
  provider_slug: string;
  provider_name: string;
  status: string;
  proxy_url?: string;
};
