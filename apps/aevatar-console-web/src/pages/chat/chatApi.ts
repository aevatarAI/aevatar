import type { AGUIEvent } from "@aevatar-react-sdk/types";
import { normalizeBackendSseFrame } from "@/shared/agui/sseFrameNormalizer";
import { readResponseError } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";
import type { ChatStudioTarget, ChatUsageSummary } from "./chatTypes";

type JsonRecord = Record<string, unknown>;

export type ChatStreamRequest = {
  prompt: string;
  sessionId: string;
};

export type ChatStreamFrame = {
  event: AGUIEvent | null;
  raw: unknown;
};

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined)
  ) as T;
}

function asRecord(value: unknown): JsonRecord | undefined {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonRecord)
    : undefined;
}

function readString(record: JsonRecord | undefined, ...keys: string[]): string {
  if (!record) {
    return "";
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }
  }

  return "";
}

function readNumber(
  record: JsonRecord | undefined,
  ...keys: string[]
): number | undefined {
  if (!record) {
    return undefined;
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "number" && Number.isFinite(value)) {
      return value;
    }

    if (typeof value === "string" && value.trim()) {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        return parsed;
      }
    }
  }

  return undefined;
}

function findNestedRecord(
  record: JsonRecord | undefined,
  ...keys: string[]
): JsonRecord | undefined {
  if (!record) {
    return undefined;
  }

  for (const key of keys) {
    const nested = asRecord(record[key]);
    if (nested) {
      return nested;
    }
  }

  return undefined;
}

function normalizeUsage(record: JsonRecord | undefined): ChatUsageSummary | null {
  if (!record) {
    return null;
  }

  const usage: ChatUsageSummary = {
    completionTokens: readNumber(record, "completionTokens", "completion_tokens"),
    cost: readNumber(record, "cost"),
    latencyMs: readNumber(record, "latencyMs", "latency_ms"),
    model: readString(record, "model") || undefined,
    promptTokens: readNumber(record, "promptTokens", "prompt_tokens"),
    totalTokens: readNumber(record, "totalTokens", "total_tokens"),
  };
  const hasUsage = Object.values(usage).some(
    (value) => value !== undefined && value !== ""
  );

  return hasUsage ? usage : null;
}

function mergeUsage(
  current: ChatUsageSummary | undefined,
  next: ChatUsageSummary | null
): ChatUsageSummary | undefined {
  if (!next) {
    return current;
  }

  return {
    ...current,
    ...Object.fromEntries(
      Object.entries(next).filter(([, value]) => value !== undefined)
    ),
  };
}

function normalizeTarget(record: JsonRecord | undefined): ChatStudioTarget | null {
  if (!record) {
    return null;
  }

  const target: ChatStudioTarget = {
    memberId: readString(record, "memberId", "member_id") || undefined,
    runId: readString(record, "runId", "run_id", "actorId", "actor_id") || undefined,
    scopeId: readString(record, "scopeId", "scope_id") || undefined,
    studioUrl: readString(record, "studioUrl", "studio_url") || undefined,
    teamId: readString(record, "teamId", "team_id") || undefined,
    workflowId: readString(record, "workflowId", "workflow_id") || undefined,
  };
  const hasTarget = Object.values(target).some(Boolean);

  return hasTarget ? target : null;
}

function mergeTarget(
  current: ChatStudioTarget | undefined,
  next: ChatStudioTarget | null
): ChatStudioTarget | undefined {
  if (!next) {
    return current;
  }

  return {
    ...current,
    ...Object.fromEntries(
      Object.entries(next).filter(([, value]) => Boolean(value))
    ),
  };
}

function unpackAnyPayload(value: unknown): JsonRecord | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }

  const fields = findNestedRecord(record, "fields");
  if (!fields) {
    return record;
  }

  const unpacked: JsonRecord = {};
  for (const [key, fieldValue] of Object.entries(fields)) {
    const field = asRecord(fieldValue);
    if (!field) {
      continue;
    }

    if (typeof field.stringValue === "string") {
      unpacked[key] = field.stringValue;
    } else if (typeof field.numberValue === "number") {
      unpacked[key] = field.numberValue;
    } else if (typeof field.boolValue === "boolean") {
      unpacked[key] = field.boolValue;
    } else if (field.structValue) {
      unpacked[key] = unpackAnyPayload(field.structValue);
    }
  }

  return unpacked;
}

export function extractChatStreamArtifacts(frames: readonly unknown[]): {
  target?: ChatStudioTarget;
  usage?: ChatUsageSummary;
} {
  let target: ChatStudioTarget | undefined;
  let usage: ChatUsageSummary | undefined;

  for (const raw of frames) {
    const frame = asRecord(raw);
    if (!frame) {
      continue;
    }

    usage = mergeUsage(usage, normalizeUsage(asRecord(frame.usage)));
    target = mergeTarget(target, normalizeTarget(frame));

    const runFinished = asRecord(frame.runFinished);
    const result = asRecord(runFinished?.result);
    usage = mergeUsage(usage, normalizeUsage(asRecord(result?.usage)));
    target = mergeTarget(target, normalizeTarget(result));

    const custom = asRecord(frame.custom);
    const customPayload =
      unpackAnyPayload(custom?.payload) ?? asRecord(custom?.payload);
    usage = mergeUsage(usage, normalizeUsage(asRecord(customPayload?.usage)));
    target = mergeTarget(target, normalizeTarget(customPayload));

    const rawObserved = asRecord(customPayload?.payload);
    usage = mergeUsage(usage, normalizeUsage(asRecord(rawObserved?.usage)));
    target = mergeTarget(target, normalizeTarget(rawObserved));
  }

  return { target, usage };
}

export async function startChatStream(
  request: ChatStreamRequest,
  signal: AbortSignal
): Promise<Response> {
  const response = await authFetch("/api/chat", {
    body: JSON.stringify(
      compactObject({
        prompt: request.prompt.trim(),
        sessionId: request.sessionId.trim(),
        workflow: "studio",
      })
    ),
    headers: {
      Accept: "text/event-stream",
      "Content-Type": "application/json",
    },
    method: "POST",
    signal,
  });

  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  return response;
}

export async function* readChatStreamFrames(
  response: Response,
  options?: { signal?: AbortSignal }
): AsyncGenerator<ChatStreamFrame, void, undefined> {
  const body = response.body;
  if (!body) {
    throw new Error("Chat response has no readable stream.");
  }

  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  const dataLines: string[] = [];

  try {
    while (!options?.signal?.aborted) {
      const { done, value } = await reader.read();
      if (done) {
        buffer += "\n";
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split("\n");
      buffer = done ? "" : (lines.pop() ?? "");

      for (const line of lines) {
        const normalizedLine = line.endsWith("\r") ? line.slice(0, -1) : line;
        if (normalizedLine === "") {
          if (dataLines.length > 0) {
            const data = dataLines.splice(0, dataLines.length).join("\n").trim();
            if (data && data !== "[DONE]") {
              try {
                const raw = JSON.parse(data);
                yield {
                  event: normalizeBackendSseFrame(raw),
                  raw,
                };
              } catch {
                // Ignore malformed frames so one bad event does not kill chat.
              }
            }
          }
          continue;
        }

        if (normalizedLine.startsWith("data:")) {
          const payload =
            normalizedLine.length > 5 ? normalizedLine.slice(5) : "";
          dataLines.push(payload.startsWith(" ") ? payload.slice(1) : payload);
        }
      }

      if (done) {
        break;
      }
    }
  } finally {
    reader.releaseLock();
  }
}
