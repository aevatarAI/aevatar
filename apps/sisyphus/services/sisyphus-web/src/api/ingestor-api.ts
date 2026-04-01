import { proxyUrl } from '../hooks/use-api'
import type { UploadHistoryItem, UploadDetail } from '../types/runner'

const SERVICE = 'sisyphus-ingestor'

function url(path: string): string {
  return proxyUrl(SERVICE, path)
}

function authHeaders(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }
}

export interface IngestPayload {
  nodes: Array<{
    type: string
    properties: Record<string, unknown>
  }>
  edges: Array<{
    source: string
    target: string
    type: string
    properties?: Record<string, unknown>
  }>
}

export interface IngestResponse {
  nodeIds: string[]
  edgeIds: string[]
}

export async function ingestContent(payload: IngestPayload, token: string): Promise<IngestResponse> {
  const res = await fetch(url('/ingest'), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to ingest content: ${res.status}`)
  return res.json()
}

export async function fetchUploadHistory(token: string): Promise<UploadHistoryItem[]> {
  const res = await fetch(url('/history'), {
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to fetch upload history: ${res.status}`)
  return res.json()
}

export async function fetchUploadDetail(id: string, token: string): Promise<UploadDetail> {
  const res = await fetch(url(`/history/${encodeURIComponent(id)}`), {
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to fetch upload detail: ${res.status}`)
  return res.json()
}
