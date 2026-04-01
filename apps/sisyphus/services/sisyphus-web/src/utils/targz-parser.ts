import pako from 'pako'

export interface RedNode {
  id: string
  type: string
  properties: Record<string, unknown>
}

export interface RedEdge {
  id: string
  source: string
  target: string
  type: string
  properties: Record<string, unknown>
}

export interface ParsedUpload {
  nodes: RedNode[]
  edges: RedEdge[]
  texContent: string[]
  metadata: Record<string, unknown>[]
}

interface TarEntry {
  name: string
  content: Uint8Array
}

/** Parse a tar archive from a Uint8Array */
function parseTar(data: Uint8Array): TarEntry[] {
  const entries: TarEntry[] = []
  let offset = 0

  while (offset + 512 <= data.length) {
    // Check for empty block (end of archive)
    let allZero = true
    for (let i = 0; i < 512; i++) {
      if (data[offset + i] !== 0) {
        allZero = false
        break
      }
    }
    if (allZero) break

    // Read filename (first 100 bytes, null-terminated)
    let nameEnd = offset
    while (nameEnd < offset + 100 && data[nameEnd] !== 0) nameEnd++
    const name = new TextDecoder().decode(data.slice(offset, nameEnd))

    // Read file size (octal, bytes 124-135)
    const sizeStr = new TextDecoder().decode(data.slice(offset + 124, offset + 136)).replace(/\0/g, '').trim()
    const size = parseInt(sizeStr, 8) || 0

    // Content starts at next 512-byte block
    const contentStart = offset + 512
    const content = data.slice(contentStart, contentStart + size)

    if (name && size > 0) {
      entries.push({ name, content })
    }

    // Move to next entry (aligned to 512 bytes)
    offset = contentStart + Math.ceil(size / 512) * 512
  }

  return entries
}

/**
 * Parse a .tar.gz file into RedNode/RedEdge format.
 * Extracts .tex and .meta.json files.
 */
export function parseTarGz(compressedData: ArrayBuffer): ParsedUpload {
  const decompressed = pako.ungzip(new Uint8Array(compressedData))
  const entries = parseTar(decompressed)
  const decoder = new TextDecoder()

  const texContent: string[] = []
  const metadata: Record<string, unknown>[] = []
  const nodes: RedNode[] = []
  const edges: RedEdge[] = []

  for (const entry of entries) {
    const text = decoder.decode(entry.content)

    if (entry.name.endsWith('.tex')) {
      texContent.push(text)

      // Create a RedNode for each .tex file
      const baseName = entry.name.replace(/\.tex$/, '').replace(/^.*\//, '')
      nodes.push({
        id: `upload-${baseName}-${Date.now()}`,
        type: 'UploadedContent',
        properties: {
          name: baseName,
          content: text,
          source_file: entry.name,
        },
      })
    } else if (entry.name.endsWith('.meta.json')) {
      try {
        const meta = JSON.parse(text) as Record<string, unknown>
        metadata.push(meta)

        // If metadata contains relationship info, create edges
        if (Array.isArray(meta.references)) {
          for (const ref of meta.references as Array<{ source?: string; target?: string; type?: string }>) {
            if (ref.source && ref.target) {
              edges.push({
                id: `upload-edge-${ref.source}-${ref.target}-${Date.now()}`,
                source: ref.source,
                target: ref.target,
                type: ref.type ?? 'REFERENCES',
                properties: {},
              })
            }
          }
        }
      } catch {
        console.warn(`Failed to parse metadata: ${entry.name}`)
      }
    }
  }

  return { nodes, edges, texContent, metadata }
}
