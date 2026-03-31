/* ─── Aevatar Workflow Studio – API client ─── */

import { getAccessToken } from './auth/nyxid';

const DEFAULT_RUNTIME_URL = 'https://aevatar-console-backend-api.aevatar.ai';
let BASE = `${DEFAULT_RUNTIME_URL}/api`;

export function setBaseUrl(runtimeUrl: string) {
  const trimmed = (runtimeUrl || DEFAULT_RUNTIME_URL).replace(/\/+$/, '');
  BASE = `${trimmed}/api`;
}

export function getBaseUrl(): string {
  return BASE;
}
const AUTH_REQUIRED_EVENT = 'aevatar:auth-required';

function getAuthHeaders(): Record<string, string> {
  const token = getAccessToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

type AuthRequiredDetail = {
  loginUrl?: string | null;
  message?: string | null;
};

function isJsonContentType(contentType: string | null) {
  const value = String(contentType || '').toLowerCase();
  return value.includes('application/json') || value.includes('+json');
}

function isHtmlContentType(contentType: string | null) {
  const value = String(contentType || '').toLowerCase();
  return value.includes('text/html') || value.includes('application/xhtml+xml');
}

function notifyAuthRequired(detail: AuthRequiredDetail) {
  if (typeof window === 'undefined') {
    return;
  }

  window.dispatchEvent(new CustomEvent<AuthRequiredDetail>(AUTH_REQUIRED_EVENT, {
    detail,
  }));
}

export function onAuthRequired(listener: (detail: AuthRequiredDetail) => void) {
  if (typeof window === 'undefined') {
    return () => undefined;
  }

  const handler = (event: Event) => {
    const detail = (event as CustomEvent<AuthRequiredDetail>).detail || {};
    listener(detail);
  };

  window.addEventListener(AUTH_REQUIRED_EVENT, handler as EventListener);
  return () => window.removeEventListener(AUTH_REQUIRED_EVENT, handler as EventListener);
}

async function request<T>(path: string, opts?: RequestInit): Promise<T> {
  const headers = new Headers(opts?.headers);
  const isFormDataBody = typeof FormData !== 'undefined' && opts?.body instanceof FormData;
  if (!isFormDataBody && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  // Inject NyxID Bearer token if available and not already set.
  if (!headers.has('Authorization')) {
    const auth = getAuthHeaders();
    if (auth.Authorization) headers.set('Authorization', auth.Authorization);
  }

  const res = await fetch(`${BASE}${path}`, {
    ...opts,
    headers,
  });

  const contentType = res.headers.get('content-type');
  if (!res.ok) {
    const body = isJsonContentType(contentType)
      ? await res.json().catch(() => ({}))
      : { message: await res.text().catch(() => '') };

    if (res.status === 401 || body?.loginUrl) {
      notifyAuthRequired({
        loginUrl: body?.loginUrl,
        message: body?.message || 'Sign in to continue.',
      });
    }

    throw { status: res.status, ...body };
  }

  if (res.status === 204) return undefined as T;
  if (isJsonContentType(contentType)) {
    return res.json();
  }

  if (res.redirected) {
    notifyAuthRequired({
      loginUrl: res.url,
      message: 'Sign in to continue.',
    });
  }

  const rawBody = await res.text().catch(() => '');
  if (isHtmlContentType(contentType) || res.redirected) {
    notifyAuthRequired({
      loginUrl: res.redirected ? res.url : null,
      message: 'API returned HTML instead of JSON. Sign-in may be required.',
    });
  }

  throw {
    status: res.redirected ? 401 : res.status,
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
      ...getAuthHeaders(),
    },
    body: JSON.stringify(body),
    signal,
  });

  if (res.redirected) {
    notifyAuthRequired({
      loginUrl: res.url,
      message: 'Sign in to continue.',
    });
  }

  if (!res.ok) {
    const payload = await res.json().catch(() => ({}));
    if (res.status === 401 || payload?.loginUrl) {
      notifyAuthRequired({
        loginUrl: payload?.loginUrl || (res.redirected ? res.url : null),
        message: payload?.message || 'Sign in to continue.',
      });
    }

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

  if (frame.textMessageReasoning) {
    return {
      type: 'TEXT_MESSAGE_REASONING',
      delta: frame.textMessageReasoning.delta || '',
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

/* ─── Connectors ─── */
export const connectors = {
  getCatalog:  ()          => request<any>('/connectors'),
  saveCatalog: (data: any) => request<any>('/connectors', { method: 'PUT', body: JSON.stringify(data) }),
  importCatalog: (file: File) => {
    const form = new FormData();
    form.set('file', file, file.name);
    return request<any>('/connectors/import', { method: 'POST', body: form });
  },
  getDraft:    ()          => request<any>('/connectors/draft'),
  saveDraft:   (data: any) => request<any>('/connectors/draft', { method: 'PUT', body: JSON.stringify(data) }),
  deleteDraft: ()          => request<void>('/connectors/draft', { method: 'DELETE' }),
};

/* ─── Roles ─── */
export const roles = {
  getCatalog:  ()          => request<any>('/roles'),
  saveCatalog: (data: any) => request<any>('/roles', { method: 'PUT', body: JSON.stringify(data) }),
  importCatalog: (file: File) => {
    const form = new FormData();
    form.set('file', file, file.name);
    return request<any>('/roles/import', { method: 'POST', body: form });
  },
  getDraft:    ()          => request<any>('/roles/draft'),
  saveDraft:   (data: any) => request<any>('/roles/draft', { method: 'PUT', body: JSON.stringify(data) }),
  deleteDraft: ()          => request<void>('/roles/draft', { method: 'DELETE' }),
};

/* ─── Settings ─── */
export const settings = {
  get:         ()          => request<any>('/settings'),
  save:        (data: any) => request<any>('/settings', { method: 'PUT', body: JSON.stringify(data) }),
  testRuntime: (data: any) => request<any>('/settings/runtime/test', { method: 'POST', body: JSON.stringify(data) }),
};

/* ─── User Config (per-user, chrono-storage backed) ─── */
export const userConfig = {
  get:    ()          => request<any>('/user-config'),
  save:   (data: any) => request<any>('/user-config', { method: 'PUT', body: JSON.stringify(data) }),
  models: ()          => request<{
    providers?: { provider_slug: string; provider_name: string; status: string; proxy_url: string }[];
    gateway_url?: string;
    supported_models?: string[];
  }>('/user-config/models'),
};

/* ─── Executions ─── */
export const executions = {
  list:  ()              => request<any[]>('/executions'),
  get:   (id: string)    => request<any>(`/executions/${id}`),
  start: (data: any)     => request<any>('/executions', { method: 'POST', body: JSON.stringify(data) }),
  resume:(id: string, data: any) => request<any>(`/executions/${id}/resume`, { method: 'POST', body: JSON.stringify(data) }),
  stop:  (id: string, data: any) => request<any>(`/executions/${id}/stop`, { method: 'POST', body: JSON.stringify(data) }),
};

export const assistant = {
  authorWorkflow: async (
    data: {
      prompt: string;
      currentYaml?: string;
      availableWorkflowNames?: string[];
      metadata?: Record<string, string>;
    },
    options?: {
      signal?: AbortSignal;
      onText?: (text: string) => void;
      onReasoning?: (text: string) => void;
    },
  ) => {
    let text = '';
    let reasoning = '';
    await streamSse('/app/workflow-generator', data, frame => {
      const normalized = normalizeAssistantFrame(frame);
      if (!normalized) {
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_CONTENT') {
        text += normalized.delta || '';
        options?.onText?.(text);
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_REASONING') {
        reasoning += normalized.delta || '';
        options?.onReasoning?.(reasoning);
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
  authorScript: async (
    data: {
      prompt: string;
      currentSource?: string;
      currentPackage?: any;
      currentFilePath?: string;
      metadata?: Record<string, string>;
    },
    options?: {
      signal?: AbortSignal;
      onText?: (text: string) => void;
      onReasoning?: (text: string) => void;
    },
  ) => {
    let text = '';
    let reasoning = '';
    let scriptPackage: any = null;
    let currentFilePath = '';
    await streamSse('/app/scripts/generator', data, frame => {
      const normalized = normalizeAssistantFrame(frame);
      if (!normalized) {
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_CONTENT') {
        text += normalized.delta || '';
        options?.onText?.(text);
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_REASONING') {
        reasoning += normalized.delta || '';
        options?.onReasoning?.(reasoning);
        return;
      }

      if (normalized.type === 'TEXT_MESSAGE_END') {
        text = text || normalized.message || normalized.delta || '';
        scriptPackage = normalized.scriptPackage || null;
        currentFilePath = normalized.currentFilePath || '';
        options?.onText?.(text);
        return;
      }

      if (normalized.type === 'RUN_ERROR') {
        throw new Error(normalized.message || 'Assistant run failed.');
      }
    }, options?.signal);

    return {
      text,
      scriptPackage,
      currentFilePath,
    };
  },
};

export const auth = {
  getSession: () => request<any>('/auth/me'),
};

/* ─── Scope / Runtime APIs (new) ─── */
export const scope = {
  /** GET /api/scopes/{scopeId}/binding — read current default scope binding */
  getBinding: (scopeId: string) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`),

  /** PUT /api/scopes/{scopeId}/binding — bind workflow as default scope service */
  bindWorkflow: (scopeId: string, workflowYamls: string[], displayName?: string) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`, {
      method: 'PUT',
      body: JSON.stringify({
        implementationKind: 'workflow',
        ...(displayName ? { displayName } : {}),
        workflowYamls,
      }),
    }),

  /** PUT /api/scopes/{scopeId}/binding — bind a static GAgent as default scope service */
  bindGAgent: (
    scopeId: string,
    actorTypeName: string,
    preferredActorId?: string,
    displayName?: string,
  ) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`, {
      method: 'PUT',
      body: JSON.stringify({
        implementationKind: 'gagent',
        ...(displayName ? { displayName } : {}),
        gagent: {
          actorTypeName,
          preferredActorId: preferredActorId || null,
          endpoints: [
            { endpointId: 'chat', displayName: 'Chat', kind: 'chat', requestTypeUrl: '', responseTypeUrl: '', description: '' },
          ],
        },
      }),
    }),

  /** POST /api/scopes/{scopeId}/workflow/draft-run — draft run with inline bundle, SSE */
  streamDraftRun: (
    scopeId: string,
    prompt: string,
    workflowYamls?: string[],
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
  ) => {
    const body: any = { prompt };
    if (workflowYamls?.length) body.workflowYamls = workflowYamls;
    return streamSse(`/scopes/${enc(scopeId)}/workflow/draft-run`, body, onFrame ?? (() => {}), signal);
  },

  /** POST /api/scopes/{scopeId}/invoke/chat:stream — scope-level default chat, SSE */
  streamDefaultChat: (
    scopeId: string,
    prompt: string,
    sessionId?: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
  ) => {
    const body: any = { prompt };
    if (sessionId) body.sessionId = sessionId;
    return streamSse(
      `/scopes/${enc(scopeId)}/invoke/chat:stream`,
      body, onFrame ?? (() => {}), signal,
    );
  },

  /** POST /api/scopes/{scopeId}/services/{serviceId}/invoke/chat:stream — service invoke, SSE */
  streamInvoke: (
    scopeId: string,
    serviceId: string,
    prompt: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
  ) => {
    const body: any = { prompt };
    return streamSse(
      `/scopes/${enc(scopeId)}/services/${enc(serviceId)}/invoke/chat:stream`,
      body, onFrame ?? (() => {}), signal,
    );
  },

  /** GET /api/services?tenantId=... — list services in scope */
  listServices: (scopeId: string, take = 20) =>
    request<any[]>(`/services?tenantId=${enc(scopeId)}&appId=default&namespace=default&take=${take}`),

  /** GET /api/actors/{actorId} — actor snapshot for run logs */
  getActorSnapshot: (actorId: string) =>
    request<any>(`/actors/${enc(actorId)}`),

  /** GET /api/actors/{actorId}/timeline — run logs timeline */
  getActorTimeline: (actorId: string, take = 50) =>
    request<any>(`/actors/${enc(actorId)}/timeline?take=${take}`),
};

// Keep legacy alias for backward compat in App.tsx
export const runtime = scope;

/* ─── NyxID Chat APIs ─── */
export const nyxidChat = {
  createConversation: (scopeId: string) =>
    request<{ actorId: string; createdAt: string }>(`/scopes/${enc(scopeId)}/nyxid-chat/conversations`, { method: 'POST' }),

  listConversations: (scopeId: string) =>
    request<Array<{ actorId: string; createdAt: string }>>(`/scopes/${enc(scopeId)}/nyxid-chat/conversations`),

  streamMessage: (
    scopeId: string,
    actorId: string,
    prompt: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
  ) =>
    streamSse(
      `/scopes/${enc(scopeId)}/nyxid-chat/conversations/${enc(actorId)}:stream`,
      { prompt },
      onFrame ?? (() => {}),
      signal,
    ),

  deleteConversation: (scopeId: string, actorId: string) =>
    request<void>(`/scopes/${enc(scopeId)}/nyxid-chat/conversations/${enc(actorId)}`, { method: 'DELETE' }),
};

/* ─── Chat History APIs ─── */
export const chatHistory = {
  getIndex: (scopeId: string) =>
    request<{ conversations: Array<{ id: string; title: string; serviceId: string; serviceKind: string; createdAt: string; updatedAt: string; messageCount: number }> }>(
      `/scopes/${enc(scopeId)}/chat-history`,
    ),

  getConversation: (scopeId: string, convId: string) =>
    request<Array<{ id: string; role: string; content: string; timestamp: number; status: string; error?: string; thinking?: string }>>(
      `/scopes/${enc(scopeId)}/chat-history/conversations/${enc(convId)}`,
    ),

  saveConversation: (scopeId: string, convId: string, meta: any, messages: any[]) =>
    request<void>(`/scopes/${enc(scopeId)}/chat-history/conversations/${enc(convId)}`, {
      method: 'PUT',
      body: JSON.stringify({ meta, messages }),
    }),

  deleteConversation: (scopeId: string, convId: string) =>
    request<void>(`/scopes/${enc(scopeId)}/chat-history/conversations/${enc(convId)}`, {
      method: 'DELETE',
    }),
};

/* ─── GAgent APIs ─── */
export const gagent = {
  /** GET /api/scopes/gagent-types — list available GAgent types */
  listTypes: () =>
    request<Array<{ typeName: string; fullName: string; assemblyName: string }>>('/scopes/gagent-types'),

  /** GET /api/scopes/{scopeId}/gagent-actors — list persisted actor entries */
  listActors: (scopeId: string) =>
    request<Array<{ gAgentType: string; actorIds: string[] }>>(`/scopes/${enc(scopeId)}/gagent-actors`),

  /** POST /api/scopes/{scopeId}/gagent-actors — persist a new actor ID entry */
  addActor: (scopeId: string, gagentType: string, actorId: string) =>
    request<void>(`/scopes/${enc(scopeId)}/gagent-actors`, {
      method: 'POST',
      body: JSON.stringify({ gagentType, actorId }),
    }),

  /** DELETE /api/scopes/{scopeId}/gagent-actors/{actorId} — remove an actor entry */
  removeActor: (scopeId: string, gagentType: string, actorId: string) =>
    request<void>(`/scopes/${enc(scopeId)}/gagent-actors/${enc(actorId)}?gagentType=${enc(gagentType)}`, {
      method: 'DELETE',
    }),

  /** POST /api/scopes/{scopeId}/gagent/draft-run — draft-run a GAgent (SSE) */
  streamDraftRun: (
    scopeId: string,
    actorTypeName: string,
    prompt: string,
    preferredActorId?: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
  ) =>
    streamSse(
      `/scopes/${enc(scopeId)}/gagent/draft-run`,
      { actorTypeName, prompt, preferredActorId: preferredActorId || null },
      onFrame ?? (() => {}),
      signal,
    ),
};

function enc(value: string) {
  return encodeURIComponent(value.trim());
}

/* ─── Ornn Skills Platform ─── */
export type OrnnSkillSummary = {
  guid?: string;
  name?: string;
  description?: string;
  isPrivate?: boolean;
  metadata?: { category?: string; tag?: string[] };
};

export type OrnnSearchResult = {
  total: number;
  totalPages: number;
  page: number;
  pageSize: number;
  items: OrnnSkillSummary[];
};

export const ornn = {
  /** Search skills on the Ornn platform directly (bearer token auth). */
  searchSkills: async (
    ornnBaseUrl: string,
    query = '',
    scope = 'mixed',
    page = 1,
    pageSize = 50,
  ): Promise<OrnnSearchResult> => {
    const url = `${ornnBaseUrl.replace(/\/+$/, '')}/api/web/skill-search?query=${encodeURIComponent(query)}&mode=keyword&scope=${encodeURIComponent(scope)}&page=${page}&pageSize=${pageSize}`;
    const res = await fetch(url, { headers: { ...getAuthHeaders() } });
    if (!res.ok) throw { status: res.status, message: `Ornn search failed: ${res.statusText}` };
    const json = await res.json();
    return json?.data || { total: 0, totalPages: 0, page: 1, pageSize, items: [] };
  },

  /** Check Ornn health / connectivity. */
  checkHealth: async (ornnBaseUrl: string): Promise<boolean> => {
    try {
      const url = `${ornnBaseUrl.replace(/\/+$/, '')}/api/web/skill-search?query=&scope=public&page=1&pageSize=1`;
      const res = await fetch(url, { headers: { ...getAuthHeaders() }, signal: AbortSignal.timeout(5000) });
      return res.ok;
    } catch {
      return false;
    }
  },
};

export const app = {
  getContext: () => request<any>('/app/context'),
  validateDraftScript: (data: any, signal?: AbortSignal) => request<any>('/app/scripts/validate', { method: 'POST', body: JSON.stringify(data), signal }),
  listScripts: (includeSource = false) => request<any>(`/app/scripts?includeSource=${includeSource ? 'true' : 'false'}`),
  getScript: (scriptId: string) => request<any>(`/app/scripts/${encodeURIComponent(scriptId)}`),
  getScriptCatalog: (scriptId: string) => request<any>(`/app/scripts/${encodeURIComponent(scriptId)}/catalog`),
  listScriptRuntimes: (take = 24) => request<any>(`/app/scripts/runtimes?take=${take}`),
  getEvolutionDecision: (proposalId: string) => request<any>(`/app/scripts/evolutions/${encodeURIComponent(proposalId)}`),
  getRuntimeReadModel: (actorId: string) => request<any>(`/app/scripts/runtimes/${encodeURIComponent(actorId)}/readmodel`),
  saveScript: (data: any) => request<any>('/app/scripts', { method: 'POST', body: JSON.stringify(data) }),
  runDraftScript: (scopeId: string, data: any) => request<any>(`/scopes/${enc(scopeId)}/scripts/draft-run`, { method: 'POST', body: JSON.stringify(data) }),
};

export const scripts = {
  getReadModel: (actorId: string) => request<any>(`/app/scripts/runtimes/${encodeURIComponent(actorId)}/readmodel`),
  proposeEvolution: (data: any) => request<any>('/app/scripts/evolutions/proposals', { method: 'POST', body: JSON.stringify(data) }),
};
