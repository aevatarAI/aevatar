import { readSseResponse } from '../utils/sse-parser'

const AEVATAR_API = import.meta.env.VITE_AEVATAR_API_URL

export interface WorkflowRunResponse {
  run_id: string
}

export interface WorkflowEvent {
  type: string
  step_name?: string
  data?: Record<string, unknown>
  error?: string
  timestamp?: string
}

function authHeaders(accessToken: string): HeadersInit {
  return {
    Authorization: `Bearer ${accessToken}`,
    'Content-Type': 'application/json',
  }
}

export async function startResearch(
  graphId: string,
  params: Record<string, unknown>,
  accessToken: string,
): Promise<WorkflowRunResponse> {
  const res = await fetch(`${AEVATAR_API}/api/workflows/sisyphus_research/run`, {
    method: 'POST',
    headers: authHeaders(accessToken),
    body: JSON.stringify({ graph_id: graphId, ...params }),
  })
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new Error(`Failed to start research workflow: ${res.status} ${body}`)
  }
  return res.json()
}

export function subscribeWorkflowEvents(
  runId: string,
  accessToken: string,
  onEvent: (event: WorkflowEvent) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  signal?: AbortSignal,
): void {
  fetch(`${AEVATAR_API}/api/workflows/runs/${encodeURIComponent(runId)}/events`, {
    headers: { Authorization: `Bearer ${accessToken}` },
    signal,
  })
    .then(async (res) => {
      if (!res.ok) {
        throw new Error(`Failed to subscribe to workflow events: ${res.status}`)
      }
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

export async function stopWorkflow(runId: string, accessToken: string): Promise<void> {
  const res = await fetch(`${AEVATAR_API}/api/workflows/runs/${encodeURIComponent(runId)}/stop`, {
    method: 'POST',
    headers: authHeaders(accessToken),
  })
  if (!res.ok && res.status !== 404) {
    throw new Error(`Failed to stop workflow: ${res.status}`)
  }
}

export async function triggerUploadIngest(
  graphId: string,
  data: Record<string, unknown>,
  accessToken: string,
): Promise<WorkflowRunResponse> {
  const res = await fetch(`${AEVATAR_API}/api/workflows/sisyphus_upload_ingest/run`, {
    method: 'POST',
    headers: authHeaders(accessToken),
    body: JSON.stringify({ graph_id: graphId, ...data }),
  })
  if (!res.ok) throw new Error(`Failed to trigger upload ingest: ${res.status}`)
  return res.json()
}

export async function triggerUploadPurify(
  graphId: string,
  accessToken: string,
): Promise<WorkflowRunResponse> {
  const res = await fetch(`${AEVATAR_API}/api/workflows/sisyphus_upload_purify/run`, {
    method: 'POST',
    headers: authHeaders(accessToken),
    body: JSON.stringify({ graph_id: graphId }),
  })
  if (!res.ok) throw new Error(`Failed to trigger upload purify: ${res.status}`)
  return res.json()
}

export async function triggerUploadVerify(
  graphId: string,
  accessToken: string,
): Promise<WorkflowRunResponse> {
  const res = await fetch(`${AEVATAR_API}/api/workflows/sisyphus_upload_verify/run`, {
    method: 'POST',
    headers: authHeaders(accessToken),
    body: JSON.stringify({ graph_id: graphId }),
  })
  if (!res.ok) throw new Error(`Failed to trigger upload verify: ${res.status}`)
  return res.json()
}
