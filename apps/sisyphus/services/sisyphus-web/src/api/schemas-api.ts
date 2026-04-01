import { proxyUrl } from '../hooks/use-api'
import type { SchemaDefinition, SchemaListItem, SchemaCreatePayload, SchemaUpdatePayload } from '../types/schema'

const SERVICE = 'sisyphus-schema'

function url(path: string): string {
  return proxyUrl(SERVICE, path)
}

function authHeaders(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }
}

export async function fetchSchemas(token: string): Promise<SchemaListItem[]> {
  const res = await fetch(url('/schemas'), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to fetch schemas: ${res.status}`)
  const data = await res.json()
  return data.schemas ?? data
}

export async function fetchSchema(id: string, token: string): Promise<SchemaDefinition> {
  const res = await fetch(url(`/schemas/${encodeURIComponent(id)}`), { headers: authHeaders(token) })
  if (!res.ok) throw new Error(`Failed to fetch schema: ${res.status}`)
  return res.json()
}

export async function createSchema(payload: SchemaCreatePayload, token: string): Promise<SchemaDefinition> {
  const res = await fetch(url('/schemas'), {
    method: 'POST',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to create schema: ${res.status}`)
  return res.json()
}

export async function updateSchema(id: string, payload: SchemaUpdatePayload, token: string): Promise<SchemaDefinition> {
  const res = await fetch(url(`/schemas/${encodeURIComponent(id)}`), {
    method: 'PUT',
    headers: authHeaders(token),
    body: JSON.stringify(payload),
  })
  if (!res.ok) throw new Error(`Failed to update schema: ${res.status}`)
  return res.json()
}

export async function deleteSchema(id: string, token: string): Promise<void> {
  const res = await fetch(url(`/schemas/${encodeURIComponent(id)}`), {
    method: 'DELETE',
    headers: authHeaders(token),
  })
  if (!res.ok) throw new Error(`Failed to delete schema: ${res.status}`)
}
