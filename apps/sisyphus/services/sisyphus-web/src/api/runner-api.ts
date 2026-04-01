import { proxyUrl } from '../hooks/use-api'
import { readSseResponse } from '../utils/sse-parser'
import type { TriggerHistoryItem, RunDetail } from '../types/runner'

const SERVICE = 'sisyphus-research-runner'

function url(path: string): string {
  return proxyUrl(SERVICE, path)
}

function authHeaders(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }
}

export interface StartRunPayload {
  workflowName: string
  params?: Record<string, unknown>
}

export interface StartRunResponse {
  runId: string
}

export async function startWorkflowRun(payload: StartRunPayload, token: string): Promise<StartRunResponse> {
  const res = await fetch(url('/runs'), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new Error(`Failed to start workflow run: ${res.status} ${body}`)
  }
  return res.json()
}

export async function stopWorkflowRun(workflowType: string, token: string): Promise<void> {
  const res = await fetch(url(`/workflows/${encodeURIComponent(workflowType)}/stop`), {
    method: 'POST',
    headers: authHeaders(token),
  })
  if (!res.ok && res.status !== 404) {
    throw new Error(`Failed to stop workflow run: ${res.status}`)
  }
}

export async function fetchRunStatus(runId: string, token: string): Promise<RunDetail> {
  const res = await fetch(url(`/runs/${encodeURIComponent(runId)}`), {
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to fetch run status: ${res.status}`)
  return res.json()
}

export async function fetchTriggerHistory(token: string): Promise<TriggerHistoryItem[]> {
  const res = await fetch(url('/history?pageSize=50'), {
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to fetch trigger history: ${res.status}`)
  const data = await res.json()
  const records = (data.records ?? data) as Array<Record<string, unknown>>
  if (!Array.isArray(records)) return []
  // Map SessionDTO → TriggerHistoryItem
  return records.map((r) => ({
    id: (r.id as string) ?? '',
    workflowName: (r.workflowType as string) ?? '',
    triggeredBy: (r.triggeredBy as string) ?? 'user',
    triggeredAt: (r.startedAt as string) ?? '',
    status: (r.status as TriggerHistoryItem['status']) ?? 'running',
    durationMs: r.duration as number | undefined,
    error: r.error as string | undefined,
  }))
}

export async function fetchRunDetail(sessionId: string, token: string): Promise<RunDetail> {
  const res = await fetch(url(`/history/${encodeURIComponent(sessionId)}`), {
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to fetch run detail: ${res.status}`)
  const data = await res.json()
  // Map SessionDetailResponse → RunDetail
  const session = data.session ?? data
  return {
    id: session.id ?? sessionId,
    workflowName: session.workflowType ?? '',
    triggeredBy: session.triggeredBy ?? 'user',
    triggeredAt: session.startedAt ?? '',
    status: session.status ?? 'unknown',
    completedAt: session.stoppedAt,
    durationMs: session.duration,
    error: session.error,
    events: (data.events ?? []).map((e: Record<string, unknown>) => ({
      type: e.eventType ?? 'unknown',
      data: e.payload as Record<string, unknown> | undefined,
      timestamp: (e.timestamp as string) ?? '',
    })),
  }
}

/** Subscribe to AG-UI events via SSE */
export function subscribeRunEvents(
  runId: string,
  token: string,
  onEvent: (event: Record<string, unknown>) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  signal?: AbortSignal,
): void {
  fetch(url(`/runs/${encodeURIComponent(runId)}/events`), {
    headers: { Authorization: `Bearer ${token}` },
    signal,
  })
    .then(async (res) => {
      if (!res.ok) throw new Error(`Failed to subscribe to run events: ${res.status}`)
      await readSseResponse(res, onEvent)
      onDone()
    })
    .catch((err) => {
      if (err.name === 'AbortError') {
        onDone()
      } else {
        onError(err)
      }
    })
}
