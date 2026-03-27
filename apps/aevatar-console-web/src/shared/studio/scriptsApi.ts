import { authFetch } from '@/shared/auth/fetch';
import type {
  DraftRunResult,
  GeneratedScriptResult,
  ScopedScriptDetail,
  ScriptCatalogSnapshot,
  ScriptPackage,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScriptValidationResult,
} from './scriptsModels';

const JSON_HEADERS = {
  'Content-Type': 'application/json',
  Accept: 'application/json',
};

type GenerateScriptRequest = {
  prompt: string;
  currentSource?: string;
  currentPackage?: ScriptPackage | null;
  currentFilePath?: string;
  metadata?: Record<string, string>;
};

type GenerateScriptOptions = {
  signal?: AbortSignal;
  onReasoning?: (delta: string) => void;
  onText?: (delta: string) => void;
};

async function scriptsFetch(
  input: string,
  init?: RequestInit,
): Promise<Response> {
  return authFetch(input, {
    credentials: 'same-origin',
    ...init,
  });
}

function isJsonContentType(contentType: string | null): boolean {
  const value = String(contentType || '').toLowerCase();
  return value.includes('application/json') || value.includes('+json');
}

async function readError(response: Response): Promise<string> {
  const text = await response.text();
  if (!text) {
    return `HTTP ${response.status}`;
  }

  try {
    const payload = JSON.parse(text) as {
      code?: string;
      error?: string;
      message?: string;
    };
    return payload.message || payload.error || payload.code || text;
  } catch {
    return text;
  }
}

async function requestJson<T>(
  input: string,
  init?: RequestInit,
): Promise<T> {
  const response = await scriptsFetch(input, init);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  if (!isJsonContentType(response.headers.get('content-type'))) {
    throw new Error('Studio script API returned an unexpected response format.');
  }

  return (await response.json()) as T;
}

type AssistantFrame = {
  type?: string;
  delta?: string;
  message?: string;
  currentFilePath?: string;
  scriptPackage?: ScriptPackage | null;
  textMessageContent?: { delta?: string };
  textMessageReasoning?: { delta?: string };
  textMessageEnd?: {
    delta?: string;
    message?: string;
    currentFilePath?: string;
    scriptPackage?: ScriptPackage | null;
  };
  runError?: { message?: string };
};

function normalizeAssistantFrame(frame: AssistantFrame): AssistantFrame | null {
  if (!frame || typeof frame !== 'object') {
    return null;
  }

  if (typeof frame.type === 'string') {
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
      delta: frame.textMessageEnd.delta || '',
      message: frame.textMessageEnd.message || '',
      currentFilePath: frame.textMessageEnd.currentFilePath || '',
      scriptPackage: frame.textMessageEnd.scriptPackage || null,
    };
  }

  if (frame.runError) {
    return {
      type: 'RUN_ERROR',
      message: frame.runError.message || 'Assistant run failed.',
    };
  }

  return null;
}

async function streamSse(
  input: string,
  body: unknown,
  onFrame: (frame: AssistantFrame) => void,
  signal?: AbortSignal,
): Promise<void> {
  const response = await scriptsFetch(input, {
    method: 'POST',
    headers: {
      Accept: 'text/event-stream',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  });

  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (!response.body) {
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { done, value } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });

    let boundary = buffer.indexOf('\n\n');
    while (boundary >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      const data = block
        .split('\n')
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trim())
        .join('\n');

      if (data && data !== '[DONE]') {
        onFrame(JSON.parse(data) as AssistantFrame);
      }

      boundary = buffer.indexOf('\n\n');
    }

    if (done) {
      break;
    }
  }
}

function scopePath(scopeId: string): string {
  return `/api/scopes/${encodeURIComponent(scopeId)}`;
}

export const scriptsApi = {
  validateDraft(
    payload: {
      scriptId: string;
      scriptRevision: string;
      source?: string;
      package?: ScriptPackage | null;
    },
    signal?: AbortSignal,
  ): Promise<ScriptValidationResult> {
    return requestJson('/api/scripts/validate', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(payload),
      signal,
    });
  },

  listScripts(scopeId: string, includeSource = false): Promise<ScopedScriptDetail[]> {
    return requestJson(
      `${scopePath(scopeId)}/scripts?includeSource=${includeSource ? 'true' : 'false'}`,
    );
  },

  getScript(scopeId: string, scriptId: string): Promise<ScopedScriptDetail> {
    return requestJson(
      `${scopePath(scopeId)}/scripts/${encodeURIComponent(scriptId)}`,
    );
  },

  getScriptCatalog(scopeId: string, scriptId: string): Promise<ScriptCatalogSnapshot> {
    return requestJson(
      `${scopePath(scopeId)}/scripts/${encodeURIComponent(scriptId)}/catalog`,
    );
  },

  saveScript(
    scopeId: string,
    payload: {
      scriptId: string;
      sourceText?: string;
      revisionId?: string;
      expectedBaseRevision?: string;
    },
  ): Promise<ScopedScriptDetail> {
    return requestJson(
      `${scopePath(scopeId)}/scripts/${encodeURIComponent(payload.scriptId)}`,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          sourceText: payload.sourceText,
          revisionId: payload.revisionId,
          expectedBaseRevision: payload.expectedBaseRevision,
        }),
      },
    );
  },

  runDraftScript(payload: {
    scopeId: string;
    scriptId?: string;
    scriptRevision?: string;
    source?: string;
    input?: string;
    definitionActorId?: string;
    runtimeActorId?: string;
    package?: ScriptPackage | null;
  }): Promise<DraftRunResult> {
    return requestJson(
      `${scopePath(payload.scopeId)}/scripts/draft-run`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          scriptId: payload.scriptId,
          scriptRevision: payload.scriptRevision,
          source: payload.source,
          input: payload.input,
          definitionActorId: payload.definitionActorId,
          runtimeActorId: payload.runtimeActorId,
          package: payload.package,
        }),
      },
    );
  },

  listRuntimes(take = 24): Promise<ScriptReadModelSnapshot[]> {
    return requestJson(`/api/app/scripts/runtimes?take=${take}`);
  },

  getRuntimeReadModel(actorId: string): Promise<ScriptReadModelSnapshot> {
    return requestJson(
      `/api/app/scripts/runtimes/${encodeURIComponent(actorId)}/readmodel`,
    );
  },

  proposeEvolution(
    scopeId: string,
    scriptId: string,
    payload: {
      baseRevision?: string;
      candidateRevision?: string;
      candidateSource?: string;
      candidateSourceHash?: string;
      reason?: string;
      proposalId?: string;
    },
  ): Promise<ScriptPromotionDecision> {
    return requestJson(
      `${scopePath(scopeId)}/scripts/${encodeURIComponent(scriptId)}/evolutions/proposals`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(payload),
      },
    );
  },

  getEvolutionDecision(proposalId: string): Promise<ScriptPromotionDecision> {
    return requestJson(
      `/api/scripts/evolutions/${encodeURIComponent(proposalId)}`,
    );
  },

  async generateScript(
    payload: GenerateScriptRequest,
    options?: GenerateScriptOptions,
  ): Promise<GeneratedScriptResult> {
    let text = '';
    let currentFilePath = '';
    let scriptPackage: ScriptPackage | null = null;

    await streamSse(
      '/api/scripts/generator',
      payload,
      (frame) => {
        const normalized = normalizeAssistantFrame(frame);
        if (!normalized) {
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_REASONING' && normalized.delta) {
          options?.onReasoning?.(normalized.delta);
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_CONTENT' && normalized.delta) {
          text += normalized.delta;
          options?.onText?.(normalized.delta);
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_END') {
          text = normalized.message || text;
          currentFilePath = normalized.currentFilePath || currentFilePath;
          scriptPackage = normalized.scriptPackage || scriptPackage;
          return;
        }

        if (normalized.type === 'RUN_ERROR') {
          throw new Error(normalized.message || 'Assistant run failed.');
        }
      },
      options?.signal,
    );

    return {
      text,
      currentFilePath,
      scriptPackage,
    };
  },
};
