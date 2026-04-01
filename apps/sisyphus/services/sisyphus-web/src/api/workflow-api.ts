import { proxyUrl } from '../hooks/use-api'
import type { WorkflowDefinition, WorkflowListItem, ConnectorDefinition } from '../types/workflow'

const SERVICE = 'sisyphus-workflow'

function url(path: string): string {
  return proxyUrl(SERVICE, path)
}

function authHeaders(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }
}

// --- Workflow CRUD ---

export async function fetchWorkflows(token: string): Promise<WorkflowListItem[]> {
  const res = await fetch(url('/workflows'), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to fetch workflows: ${res.status}`)
  const data = await res.json()
  return data.workflows ?? data
}

export async function fetchWorkflow(id: string, token: string): Promise<WorkflowDefinition> {
  const res = await fetch(url(`/workflows/${encodeURIComponent(id)}`), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to fetch workflow: ${res.status}`)
  return res.json()
}

export async function createWorkflow(
  payload: { name: string; description?: string; yaml: string },
  token: string,
): Promise<WorkflowDefinition> {
  const res = await fetch(url('/workflows'), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to create workflow: ${res.status}`)
  return res.json()
}

export async function updateWorkflow(
  id: string,
  payload: { name?: string; description?: string; yaml?: string },
  token: string,
): Promise<WorkflowDefinition> {
  const res = await fetch(url(`/workflows/${encodeURIComponent(id)}`), {
    method: 'PUT',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to update workflow: ${res.status}`)
  return res.json()
}

export async function deleteWorkflow(id: string, token: string): Promise<void> {
  const res = await fetch(url(`/workflows/${encodeURIComponent(id)}`), {
    method: 'DELETE',
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to delete workflow: ${res.status}`)
}

// --- Compile & Deploy ---

export async function compileWorkflow(id: string, token: string): Promise<{ workflowYaml: string; connectorJson: object[] }> {
  const res = await fetch(url(`/compile/${encodeURIComponent(id)}`), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify({ userToken: token }),
  })
  if (!res.ok) throw new Error(`Failed to compile workflow: ${res.status}`)
  return res.json()
}

export async function deployWorkflow(id: string, token: string): Promise<{ success: boolean; workflowId: string; revisionId?: string }> {
  const res = await fetch(url(`/deploy/${encodeURIComponent(id)}`), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify({ userToken: token }),
  })
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new Error(`Failed to deploy workflow: ${res.status} ${body}`)
  }
  return res.json()
}

// --- Connector CRUD + Compile/Sync ---

export async function fetchConnectors(token: string): Promise<ConnectorDefinition[]> {
  const res = await fetch(url('/connectors'), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to fetch connectors: ${res.status}`)
  const data = await res.json()
  return data.connectors ?? data
}

export async function createConnector(
  payload: Record<string, unknown>,
  token: string,
): Promise<ConnectorDefinition> {
  const res = await fetch(url('/connectors'), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to create connector: ${res.status}`)
  return res.json()
}

export async function updateConnector(
  id: string,
  payload: Record<string, unknown>,
  token: string,
): Promise<ConnectorDefinition> {
  const res = await fetch(url(`/connectors/${encodeURIComponent(id)}`), {
    method: 'PUT',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to update connector: ${res.status}`)
  return res.json()
}

export async function deleteConnector(id: string, token: string): Promise<void> {
  const res = await fetch(url(`/connectors/${encodeURIComponent(id)}`), {
    method: 'DELETE',
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to delete connector: ${res.status}`)
}

export async function compileConnector(id: string, token: string): Promise<Record<string, unknown>> {
  const res = await fetch(url(`/connectors/${encodeURIComponent(id)}/compile`), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to compile connector: ${res.status}`)
  return res.json()
}

export async function syncConnectors(token: string): Promise<{ success: boolean; count: number }> {
  const res = await fetch(url('/connectors/sync'), {
    method: 'POST',
    headers: authHeaders(token),
  })
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new Error(`Failed to sync connectors: ${res.status} ${body}`)
  }
  return res.json()
}
