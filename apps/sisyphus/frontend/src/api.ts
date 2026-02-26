import type { SSEEvent } from './types'

export async function fetchGraphId(): Promise<string> {
  const res = await fetch('/api/graph-id')
  if (!res.ok) throw new Error(`Failed to fetch graph ID: ${res.status}`)
  const data = await res.json()
  return data.graphId
}

export async function fetchWorkflows(): Promise<string[]> {
  const res = await fetch('/api/workflows')
  if (!res.ok) throw new Error(`Failed to fetch workflows: ${res.status}`)
  return res.json()
}

export async function fetchWorkflowYaml(name: string): Promise<string> {
  const res = await fetch(`/api/workflows/${encodeURIComponent(name)}`)
  if (!res.ok) throw new Error(`Failed to fetch workflow YAML: ${res.status}`)
  return res.text()
}

export function startResearchStream(
  params: {
    prompt: string
    workflow?: string
    agentId?: string
  },
  onEvent: (event: SSEEvent) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  signal?: AbortSignal,
): void {
  fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(params),
    signal,
  })
    .then(async (res) => {
      if (!res.ok) {
        throw new Error(`Chat request failed: ${res.status}`)
      }
      const reader = res.body?.getReader()
      if (!reader) {
        throw new Error('No response body')
      }

      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''

        for (const line of lines) {
          const trimmed = line.trim()
          if (!trimmed.startsWith('data: ')) continue
          const jsonStr = trimmed.slice(6)
          if (!jsonStr) continue
          try {
            const event: SSEEvent = JSON.parse(jsonStr)
            onEvent(event)
          } catch {
            // skip malformed JSON
          }
        }
      }

      // Process remaining buffer
      if (buffer.trim().startsWith('data: ')) {
        try {
          const event: SSEEvent = JSON.parse(buffer.trim().slice(6))
          onEvent(event)
        } catch {
          // skip
        }
      }

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
