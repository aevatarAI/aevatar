export type YamlBrowserSource = 'workflow' | 'playground';

export function buildYamlBrowserRoute(options?: {
  workflow?: string;
  source?: YamlBrowserSource;
}): string {
  const params = new URLSearchParams();
  if (options?.workflow?.trim()) {
    params.set('workflow', options.workflow.trim());
  }
  if (options?.source === 'playground') {
    params.set('source', 'playground');
  }

  const query = params.toString();
  return query ? `/yaml?${query}` : '/yaml';
}

export function buildPlaygroundRoute(options?: {
  template?: string;
  importTemplate?: boolean;
  prompt?: string;
}): string {
  const params = new URLSearchParams();
  if (options?.template?.trim()) {
    params.set('template', options.template.trim());
  }
  if (options?.importTemplate) {
    params.set('import', '1');
  }
  if (options?.prompt?.trim()) {
    params.set('prompt', options.prompt.trim());
  }

  const query = params.toString();
  return query ? `/playground?${query}` : '/playground';
}
