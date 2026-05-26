import { authFetch } from "@/shared/auth/fetch";
import { readResponseError } from "./http/error";

const STREAM_HEADERS = {
  "Content-Type": "application/json",
  Accept: "text/event-stream",
};

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined)
  ) as T;
}

export type NyxIdApprovalDecisionRequest = {
  requestId: string;
  approved: boolean;
  reason?: string;
  sessionId?: string;
};

export const nyxIdChatApi = {
  async approveToolCall(
    scopeId: string,
    actorId: string,
    request: NyxIdApprovalDecisionRequest,
    signal?: AbortSignal
  ): Promise<Response> {
    const response = await authFetch(
      `/api/scopes/${encodeSegment(
        scopeId
      )}/nyxid-chat/conversations/${encodeSegment(actorId)}:approve`,
      {
        body: JSON.stringify(
          compactObject({
            approved: request.approved,
            reason: request.reason?.trim() || undefined,
            requestId: request.requestId.trim(),
            sessionId: request.sessionId?.trim() || undefined,
          })
        ),
        headers: STREAM_HEADERS,
        method: "POST",
        signal,
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return response;
  },
};
