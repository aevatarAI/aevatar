/* ─── Aevatar Workflow Studio – API client ─── */

import { getAccessToken } from './auth/nyxid';

const BASE = '/api';
const AUTH_REQUIRED_EVENT = 'aevatar:auth-required';
const CHRONO_STORAGE_SERVICE_ERROR_CODE = 'chrono_storage_service_unavailable';
const CHRONO_STORAGE_SERVICE_DEPENDENCY = 'chrono-storage-service';
const DEFAULT_CHRONO_STORAGE_SERVICE_MESSAGE = 'Studio could not access chrono-storage-service. It may not be enabled for this host, the service may be unavailable, or NyxID may be configured to require approval for every proxy request.';
const SCOPE_SERVICE_DEFAULT_APP_ID = 'default';
const SCOPE_SERVICE_DEFAULT_NAMESPACE = 'default';

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

function createRequestHeaders(opts?: RequestInit) {
  const headers = new Headers(opts?.headers);
  const isFormDataBody = typeof FormData !== 'undefined' && opts?.body instanceof FormData;
  if (!isFormDataBody && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (!headers.has('Authorization')) {
    const auth = getAuthHeaders();
    if (auth.Authorization) {
      headers.set('Authorization', auth.Authorization);
    }
  }

  return headers;
}

async function parseErrorResponse(res: Response): Promise<never> {
  const contentType = res.headers.get('content-type');
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
  const headers = createRequestHeaders(opts);

  const res = await fetch(`${BASE}${path}`, {
    ...opts,
    headers,
    credentials: 'include',
  });

  const contentType = res.headers.get('content-type');
  if (!res.ok) {
    return parseErrorResponse(res);
  }

  if (res.status === 204) return undefined as T;
  // Handle empty body (e.g. DELETE returning 200 with Content-Length: 0)
  const contentLength = res.headers.get('content-length');
  if (contentLength === '0' || (!contentType && contentLength === '0')) return undefined as T;
  if (isJsonContentType(contentType)) {
    return res.json();
  }
  // No Content-Type but body might be empty — try to read and return if empty
  if (!contentType) {
    const text = await res.text();
    if (!text.trim()) return undefined as T;
    try { return JSON.parse(text); } catch { /* fall through */ }
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

export type ScopeScriptCommandAcceptedHandle = {
  actorId: string;
  commandId: string;
  correlationId: string;
};

export type ScopeScriptAcceptedSummary = {
  scopeId: string;
  scriptId: string;
  catalogActorId: string;
  definitionActorId: string;
  revisionId: string;
  sourceHash: string;
  acceptedAt: string;
  proposalId: string;
  expectedBaseRevision: string;
};

export type AppScopeScriptSaveAcceptedResponse = {
  acceptedScript: ScopeScriptAcceptedSummary;
  submittedSource: {
    sourceText: string;
    definitionActorId: string;
    revision: string;
    sourceHash: string;
  };
  definitionCommand: ScopeScriptCommandAcceptedHandle;
  catalogCommand: ScopeScriptCommandAcceptedHandle;
  scopeId: string;
  scriptId: string;
  revisionId: string;
  catalogActorId: string;
  definitionActorId: string;
  sourceHash: string;
  acceptedAt: string;
  proposalId: string;
  expectedBaseRevision: string;
};

export type AppScopeScriptSaveObservationRequest = {
  revisionId: string;
  definitionActorId: string;
  sourceHash: string;
  proposalId: string;
  expectedBaseRevision: string;
  acceptedAt: string;
};

export type AppScopeScriptSaveObservationResult = {
  scopeId: string;
  scriptId: string;
  status: 'pending' | 'applied' | 'rejected';
  message: string;
  currentScript: any | null;
  isTerminal: boolean;
};

async function requestText(path: string, opts?: RequestInit): Promise<string> {
  const headers = createRequestHeaders(opts);
  const res = await fetch(`${BASE}${path}`, {
    ...opts,
    headers,
    credentials: 'include',
  });

  const contentType = res.headers.get('content-type');
  if (!res.ok) {
    return parseErrorResponse(res);
  }

  if (res.redirected || isHtmlContentType(contentType)) {
    notifyAuthRequired({
      loginUrl: res.redirected ? res.url : null,
      message: 'API returned HTML instead of the requested file. Sign-in may be required.',
    });
    throw {
      status: res.redirected ? 401 : res.status,
      message: 'API returned HTML instead of the requested file. Sign-in may be required.',
    };
  }

  return res.text();
}

export function isChronoStorageServiceError(error: any) {
  return Boolean(
    error?.code === CHRONO_STORAGE_SERVICE_ERROR_CODE ||
    error?.dependency === CHRONO_STORAGE_SERVICE_DEPENDENCY ||
    (typeof error?.message === 'string' && error.message.includes('chrono-storage-service'))
  );
}

export function getChronoStorageServiceErrorMessage(error: any) {
  if (typeof error?.message === 'string' && error.message.trim()) {
    return error.message.trim();
  }

  return DEFAULT_CHRONO_STORAGE_SERVICE_MESSAGE;
}

export function buildScopeServiceQuery(
  scopeId: string,
  extraParameters?: Record<string, string>,
) {
  const query = new URLSearchParams();
  const normalizedScopeId = String(scopeId || '').trim();
  if (normalizedScopeId) {
    query.set('tenantId', normalizedScopeId);
    query.set('appId', SCOPE_SERVICE_DEFAULT_APP_ID);
    query.set('namespace', SCOPE_SERVICE_DEFAULT_NAMESPACE);
  }

  if (extraParameters) {
    for (const [key, value] of Object.entries(extraParameters)) {
      query.set(key, value);
    }
  }

  return query.toString();
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
    credentials: 'include',
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

/* ─── User Config (per-user, actor-backed via UserConfigGAgent) ─── */
export const userConfig = {
  get:    ()          => request<any>('/user-config'),
  save:   (data: any) => request<any>('/user-config', { method: 'PUT', body: JSON.stringify(data) }),
  models: ()          => request<{
    providers?: { provider_slug: string; provider_name: string; status: string; proxy_url: string; source?: string }[];
    gateway_url?: string;
    supported_models?: string[];
    models_by_provider?: Record<string, string[]>;
  }>('/user-config/models'),
};

/* ─── Executions (runtime) ─── */
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

/* ─── Scope / Runtime APIs (remote runtime) ─── */
export const scope = {
  /** GET /api/scopes/{scopeId}/binding — read current default scope binding */
  getBinding: (scopeId: string) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`),

  /** PUT /api/scopes/{scopeId}/binding — bind workflow as a scope service */
  bindWorkflow: (scopeId: string, workflowYamls: string[], displayName?: string, serviceId?: string) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`, {
      method: 'PUT',
      body: JSON.stringify({
        implementationKind: 'workflow',
        ...(displayName ? { displayName } : {}),
        ...(serviceId ? { serviceId } : {}),
        workflowYamls,
      }),
    }),

  /** PUT /api/scopes/{scopeId}/binding — bind a static GAgent as a scope service */
  bindGAgent: (
    scopeId: string,
    actorTypeName: string,
    displayName?: string,
    serviceId?: string,
  ) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`, {
      method: 'PUT',
      body: JSON.stringify({
        implementationKind: 'gagent',
        ...(displayName ? { displayName } : {}),
        ...(serviceId ? { serviceId } : {}),
        gagent: {
          actorTypeName,
          endpoints: [
            { endpointId: 'chat', displayName: 'Chat', kind: 'chat', requestTypeUrl: '', responseTypeUrl: '', description: '' },
          ],
        },
      }),
    }),

  /** PUT /api/scopes/{scopeId}/binding — bind a script as a scope service */
  bindScript: (
    scopeId: string,
    scriptId: string,
    displayName?: string,
    serviceId?: string,
    scriptRevision?: string,
  ) =>
    request<any>(`/scopes/${enc(scopeId)}/binding`, {
      method: 'PUT',
      body: JSON.stringify({
        implementationKind: 'script',
        ...(displayName ? { displayName } : {}),
        ...(serviceId ? { serviceId } : {}),
        script: {
          scriptId,
          ...(scriptRevision ? { scriptRevision } : {}),
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
    actorId?: string,
    sessionId?: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
    headers?: Record<string, string>,
  ) => {
    const body: any = { prompt };
    if (actorId) body.actorId = actorId;
    if (sessionId) body.sessionId = sessionId;
    if (headers && Object.keys(headers).length > 0) body.headers = headers;
    return streamSse(
      `/scopes/${enc(scopeId)}/invoke/chat:stream`,
      body, onFrame ?? (() => {}), signal,
    );
  },

  /** POST /api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream — service invoke, SSE */
  streamInvoke: (
    scopeId: string,
    serviceId: string,
    prompt: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
    endpointId: string = 'chat',
    headers?: Record<string, string>,
    actorId?: string,
    inputParts?: Array<{ type: string; text?: string; dataBase64?: string; mediaType?: string; uri?: string; name?: string }>,
  ) => {
    const body: any = { prompt };
    if (headers && Object.keys(headers).length > 0) body.headers = headers;
    if (actorId) body.actorId = actorId;
    if (inputParts && inputParts.length > 0) body.inputParts = inputParts;
    return streamSse(
      `/scopes/${enc(scopeId)}/services/${enc(serviceId)}/invoke/${enc(endpointId)}:stream`,
      body, onFrame ?? (() => {}), signal,
    );
  },

  /** GET /api/services?take=... — list services for the current scope or authenticated identity */
  listServices: (scopeId: string, take = 20) =>
    request<any[]>(`/services?${buildScopeServiceQuery(scopeId, { take: String(take) })}`),

  /** POST /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}:resume — resume a suspended workflow run (human_input) */
  resumeRun: (
    scopeId: string,
    serviceId: string,
    runId: string,
    data: { stepId: string; userInput?: string; approved?: boolean; actorId?: string },
  ) =>
    request<any>(`/scopes/${enc(scopeId)}/services/${enc(serviceId)}/runs/${enc(runId)}:resume`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  /** GET /api/actors/{actorId} — actor snapshot for run logs */
  getActorSnapshot: (actorId: string) =>
    request<any>(`/actors/${enc(actorId)}`),

  /** GET /api/actors/{actorId}/timeline — run logs timeline */
  getActorTimeline: (actorId: string, take = 50) =>
    request<any>(`/actors/${enc(actorId)}/timeline?take=${take}`),
};

// Keep legacy alias for backward compat in App.tsx
export const runtime = scope;

/* ─── NyxID Chat APIs (runtime) ─── */
export const nyxidChat = {
  createConversation: (scopeId: string) =>
    request<{ actorId: string }>(`/scopes/${enc(scopeId)}/nyxid-chat/conversations`, { method: 'POST' }),

  listConversations: (scopeId: string) =>
    request<unknown>(`/scopes/${enc(scopeId)}/nyxid-chat/conversations`)
      .then(decodeNyxIdConversationList),

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

  /** Send tool approval decision and stream the continuation response. */
  approveToolCall: (
    scopeId: string,
    actorId: string,
    requestId: string,
    approved: boolean,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
    sessionId?: string,
    reason?: string,
  ) =>
    streamSse(
      `/scopes/${enc(scopeId)}/nyxid-chat/conversations/${enc(actorId)}:approve`,
      { requestId, approved, reason: reason ?? '', sessionId: sessionId ?? '' },
      onFrame ?? (() => {}),
      signal,
    ),
};

/* ─── Streaming Proxy APIs (runtime) ─── */
export const streamingProxy = {
  createRoom: (scopeId: string, roomName?: string) =>
    request<{ roomId: string; roomName: string; createdAt: string }>(
      `/scopes/${enc(scopeId)}/streaming-proxy/rooms`,
      { method: 'POST', body: JSON.stringify(roomName ? { roomName } : {}) },
    ),

  streamChat: (
    scopeId: string,
    roomId: string,
    prompt: string,
    onFrame?: (frame: any) => void,
    signal?: AbortSignal,
    sessionId?: string,
    llmRoute?: string,
    llmModel?: string,
  ) => {
    const body: any = { prompt };
    if (sessionId) body.sessionId = sessionId;
    if (llmRoute !== undefined) body.llmRoute = llmRoute;
    if (llmModel !== undefined) body.llmModel = llmModel;
    return streamSse(
      `/scopes/${enc(scopeId)}/streaming-proxy/rooms/${enc(roomId)}:chat`,
      body,
      onFrame ?? (() => {}),
      signal,
    );
  },

  deleteRoom: (scopeId: string, roomId: string) =>
    request<void>(`/scopes/${enc(scopeId)}/streaming-proxy/rooms/${enc(roomId)}`, { method: 'DELETE' }),
};

/* ─── Chat History APIs (local, chrono-storage backed) ─── */
export const chatHistory = {
  getIndex: (scopeId: string) =>
    request<{ conversations: Array<{ id: string; title: string; serviceId: string; serviceKind: string; createdAt: string; updatedAt: string; messageCount: number; llmRoute?: string; llmModel?: string }> }>(
      `/scopes/${enc(scopeId)}/chat-history`,
    ),

  getConversation: (scopeId: string, convId: string) =>
    request<Array<{ id: string; role: string; content: string; authorId?: string; authorName?: string; timestamp: number; status: string; error?: string; thinking?: string }>>(
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

type GAgentActorGroup = { gAgentType: string; actorIds: string[] };
type NyxIdConversationSummary = { actorId: string };

function decodeGAgentActorGroupList(value: unknown): GAgentActorGroup[] {
  const source = Array.isArray(value)
    ? value
    : Array.isArray((value as { groups?: unknown } | null)?.groups)
      ? (value as { groups: unknown[] }).groups
      : Array.isArray((value as { Groups?: unknown } | null)?.Groups)
        ? (value as { Groups: unknown[] }).Groups
        : [];

  return source.map((entry) => {
    const record = entry as {
      gAgentType?: unknown;
      GAgentType?: unknown;
      actorIds?: unknown;
      ActorIds?: unknown;
    };
    const actorIds = Array.isArray(record.actorIds)
      ? record.actorIds
      : Array.isArray(record.ActorIds)
        ? record.ActorIds
        : [];
    return {
      gAgentType: String(record.gAgentType ?? record.GAgentType ?? ''),
      actorIds: actorIds.map((actorId) => String(actorId)),
    };
  });
}

function decodeNyxIdConversationList(value: unknown): NyxIdConversationSummary[] {
  const source = Array.isArray(value)
    ? value
    : Array.isArray((value as { conversations?: unknown } | null)?.conversations)
      ? (value as { conversations: unknown[] }).conversations
      : Array.isArray((value as { Conversations?: unknown } | null)?.Conversations)
        ? (value as { Conversations: unknown[] }).Conversations
        : [];

  return source.map((entry) => {
    const record = entry as { actorId?: unknown; ActorId?: unknown };
    return { actorId: String(record.actorId ?? record.ActorId ?? '') };
  });
}

/* ─── GAgent APIs (runtime) ─── */
export const gagent = {
  /** GET /api/scopes/gagent-types — list available GAgent types */
  listTypes: () =>
    request<Array<{ typeName: string; fullName: string; assemblyName: string }>>('/scopes/gagent-types'),

  /** GET /api/scopes/{scopeId}/gagent-actors — list persisted actor entries */
  listActors: (scopeId: string) =>
    request<unknown>(`/scopes/${enc(scopeId)}/gagent-actors`)
      .then(decodeGAgentActorGroupList),

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

/* ─── Explorer (manifest-based chrono-storage) ─── */
const encodeExplorerKey = (key: string) => key.split('/').map(segment => encodeURIComponent(segment)).join('/');

export const explorer = {
  getManifest: () => request<{ version: number; files: Array<{ key: string; type: string; name?: string; updatedAt?: string }> }>('/explorer/manifest'),
  getFile: (key: string) => requestText(`/explorer/files/${encodeExplorerKey(key)}`),
  getFileUrl: (key: string) => `${BASE}/explorer/files/${encodeExplorerKey(key)}`,
  getFileBlob: async (key: string): Promise<Blob> => {
    const resp = await fetch(`${BASE}/explorer/files/${encodeExplorerKey(key)}`, {
      headers: getAuthHeaders(),
      credentials: 'include',
    });
    if (!resp.ok) throw new Error(`Failed to fetch file: ${resp.status}`);
    return resp.blob();
  },
  putFile: (key: string, content: string) => request<void>(`/explorer/files/${encodeExplorerKey(key)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'text/plain' },
    body: content,
  }),
  uploadFile: async (key: string, file: File): Promise<{ key: string; size: number; contentType: string }> => {
    const form = new FormData();
    form.append('file', file);
    const resp = await fetch(`${BASE}/explorer/upload/${encodeExplorerKey(key)}`, {
      method: 'POST',
      headers: getAuthHeaders(),
      body: form,
      credentials: 'include',
    });
    if (!resp.ok) throw new Error(`Upload failed: ${resp.status}`);
    return resp.json();
  },
  deleteFile: (key: string) => request<void>(`/explorer/files/${encodeExplorerKey(key)}`, {
    method: 'DELETE',
  }),
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
  saveScript: (data: any) => request<AppScopeScriptSaveAcceptedResponse>('/app/scripts', { method: 'POST', body: JSON.stringify(data) }),
  observeScriptSave: (scriptId: string, data: AppScopeScriptSaveObservationRequest) =>
    request<AppScopeScriptSaveObservationResult>(`/app/scripts/${encodeURIComponent(scriptId)}/save-observation`, { method: 'POST', body: JSON.stringify(data) }),
  runDraftScript: (scopeId: string, data: any) => request<any>(`/scopes/${enc(scopeId)}/scripts/draft-run`, { method: 'POST', body: JSON.stringify(data) }),
};

export const scripts = {
  getReadModel: (actorId: string) => request<any>(`/app/scripts/runtimes/${encodeURIComponent(actorId)}/readmodel`),
  proposeEvolution: (data: any) => request<any>('/app/scripts/evolutions/proposals', { method: 'POST', body: JSON.stringify(data) }),
};
