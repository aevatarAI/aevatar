/* ─── Aevatar Workflow Studio – API client ─── */

const BASE = '/api';

function isJsonContentType(contentType: string | null) {
  const value = String(contentType || '').toLowerCase();
  return value.includes('application/json') || value.includes('+json');
}

function isHtmlContentType(contentType: string | null) {
  const value = String(contentType || '').toLowerCase();
  return value.includes('text/html') || value.includes('application/xhtml+xml');
}

function redirectToLogin(loginUrl?: string | null) {
  if (!loginUrl || typeof window === 'undefined') {
    return;
  }

  window.location.assign(loginUrl);
}

async function request<T>(path: string, opts?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json', ...opts?.headers },
    ...opts,
  });

  const contentType = res.headers.get('content-type');
  if (!res.ok) {
    const body = isJsonContentType(contentType)
      ? await res.json().catch(() => ({}))
      : { message: await res.text().catch(() => '') };

    if (body?.loginUrl) {
      redirectToLogin(body.loginUrl);
    }

    throw { status: res.status, ...body };
  }

  if (res.status === 204) return undefined as T;
  if (isJsonContentType(contentType)) {
    return res.json();
  }

  if (res.redirected) {
    redirectToLogin(res.url);
  }

  const rawBody = await res.text().catch(() => '');
  throw {
    status: res.status,
    message: isHtmlContentType(contentType)
      ? 'API returned HTML instead of JSON. Sign-in may be required.'
      : 'API returned an unexpected response format.',
    rawBody,
  };
}

async function streamSse(
  path: string,
  body: unknown,
  onFrame: (frame: any) => void,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: {
      Accept: 'text/event-stream',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  });

  if (!res.ok) {
    const payload = await res.json().catch(() => ({}));
    throw { status: res.status, ...payload };
  }

  if (!res.body) {
    return;
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });

    let boundary = buffer.indexOf('\n\n');
    while (boundary >= 0) {
      const eventBlock = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      const data = eventBlock
        .split('\n')
        .filter(line => line.startsWith('data:'))
        .map(line => line.slice(5).trim())
        .join('\n');

      if (data && data !== '[DONE]') {
        onFrame(JSON.parse(data));
      }

      boundary = buffer.indexOf('\n\n');
    }

    if (done) {
      break;
    }
  }
}

function normalizeAssistantFrame(frame: any) {
  if (!frame || typeof frame !== 'object') {
    return null;
  }

  if (frame.type) {
    return frame;
  }

  if (frame.textMessageContent) {
    return {
      type: 'TEXT_MESSAGE_CONTENT',
      delta: frame.textMessageContent.delta || '',
    };
  }

  if (frame.textMessageEnd) {
    return {
      type: 'TEXT_MESSAGE_END',
      message: frame.textMessageEnd.message || '',
      delta: frame.textMessageEnd.delta || '',
    };
  }

  if (frame.runError) {
    return {
      type: 'RUN_ERROR',
      message: frame.runError.message || 'Assistant run failed.',
    };
  }

  return frame;
}

/* ─── Bundles ─── */
export const bundles = {
  list:     ()                           => request<any[]>('/bundles'),
  get:      (id: string)                 => request<any>(`/bundles/${id}`),
  create:   (data: any)                  => request<any>('/bundles', { method: 'POST', body: JSON.stringify(data) }),
  update:   (id: string, data: any)      => request<any>(`/bundles/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  delete:   (id: string)                 => request<void>(`/bundles/${id}`, { method: 'DELETE' }),
  clone:    (id: string, data?: any)     => request<any>(`/bundles/${id}/clone`, { method: 'POST', body: JSON.stringify(data ?? {}) }),
  versions: (id: string)                 => request<any[]>(`/bundles/${id}/versions`),
  import_:  (data: any)                  => request<any>('/bundles/import', { method: 'POST', body: JSON.stringify(data) }),
  export_:  (id: string)                 => request<any>(`/bundles/${id}/export`),
};

/* ─── Editor ─── */
export const editor = {
  parseYaml:     (yaml: string, availableWorkflowNames?: string[]) => request<any>('/editor/parse-yaml',     { method: 'POST', body: JSON.stringify({ yaml, availableWorkflowNames }) }),
  serializeYaml: (document: any, availableWorkflowNames?: string[]) => request<any>('/editor/serialize-yaml', { method: 'POST', body: JSON.stringify({ document, availableWorkflowNames }) }),
  validate:      (document: any, availableWorkflowNames?: string[]) => request<any>('/editor/validate',       { method: 'POST', body: JSON.stringify({ document, availableWorkflowNames }) }),
  normalize:     (document: any, availableWorkflowNames?: string[]) => request<any>('/editor/normalize',      { method: 'POST', body: JSON.stringify({ document, availableWorkflowNames }) }),
  diff:          (a: any, b: any) => request<any>('/editor/diff',         { method: 'POST', body: JSON.stringify({ before: a, after: b }) }),
};

/* ─── Workspace ─── */
export const workspace = {
  getSettings:    ()              => request<any>('/workspace'),
  updateSettings: (data: any)     => request<any>('/workspace/settings', { method: 'PUT', body: JSON.stringify(data) }),
  addDirectory:   (data: any)     => request<any>('/workspace/directories', { method: 'POST', body: JSON.stringify(data) }),
  removeDirectory:(id: string)    => request<any>(`/workspace/directories/${id}`, { method: 'DELETE' }),
  listWorkflows:  ()              => request<any[]>('/workspace/workflows'),
  getWorkflow:    (id: string)    => request<any>(`/workspace/workflows/${id}`),
  saveWorkflow:   (data: any)     => request<any>('/workspace/workflows', { method: 'POST', body: JSON.stringify(data) }),
};

/* ─── Layout ─── */
export const layout = {
  get:  (bundleId: string)              => request<any>(`/bundles/${bundleId}/layout`),
  save: (bundleId: string, data: any)   => request<any>(`/bundles/${bundleId}/layout`, { method: 'PUT', body: JSON.stringify(data) }),
};

/* ─── Connectors ─── */
export const connectors = {
  getCatalog:   ()          => request<any>('/connectors'),
  saveCatalog:  (data: any) => request<any>('/connectors', { method: 'PUT', body: JSON.stringify(data) }),
  getDraft:     ()          => request<any>('/connectors/draft'),
  saveDraft:    (data: any) => request<any>('/connectors/draft', { method: 'PUT', body: JSON.stringify(data) }),
  deleteDraft:  ()          => request<void>('/connectors/draft', { method: 'DELETE' }),
};

/* ─── Roles ─── */
export const roles = {
  getCatalog:   ()          => request<any>('/roles'),
  saveCatalog:  (data: any) => request<any>('/roles', { method: 'PUT', body: JSON.stringify(data) }),
  getDraft:     ()          => request<any>('/roles/draft'),
  saveDraft:    (data: any) => request<any>('/roles/draft', { method: 'PUT', body: JSON.stringify(data) }),
  deleteDraft:  ()          => request<void>('/roles/draft', { method: 'DELETE' }),
};

/* ─── Settings ─── */
export const settings = {
  get:         ()          => request<any>('/settings'),
  save:        (data: any) => request<any>('/settings', { method: 'PUT', body: JSON.stringify(data) }),
  testRuntime: (data: any) => request<any>('/settings/runtime/test', { method: 'POST', body: JSON.stringify(data) }),
};

/* ─── Executions ─── */
export const executions = {
  list:  ()              => request<any[]>('/executions'),
  get:   (id: string)    => request<any>(`/executions/${id}`),
  start: (data: any)     => request<any>('/executions', { method: 'POST', body: JSON.stringify(data) }),
  resume:(id: string, data: any) => request<any>(`/executions/${id}/resume`, { method: 'POST', body: JSON.stringify(data) }),
};

export const assistant = {
  authorWorkflow: async (
    data: {
      prompt: string;
      metadata?: Record<string, string>;
      workflowYamls?: string[];
    },
    options?: {
      signal?: AbortSignal;
      onText?: (text: string) => void;
    },
  ) => {
    let text = '';
    await streamSse('/app/chat', data, frame => {
      const normalized = normalizeAssistantFrame(frame);
      if (!normalized) {
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_CONTENT') {
        text += normalized.delta || '';
        options?.onText?.(text);
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_END') {
        text = text || normalized.message || normalized.delta || '';
        options?.onText?.(text);
        return;
      }

      if (normalized.type === 'RUN_ERROR') {
        throw new Error(normalized.message || 'Assistant run failed.');
      }
    }, options?.signal);

    return text;
  },
};

export const auth = {
  getSession: () => request<any>('/auth/me'),
};

export const app = {
  getContext: () => request<any>('/app/context'),
};
