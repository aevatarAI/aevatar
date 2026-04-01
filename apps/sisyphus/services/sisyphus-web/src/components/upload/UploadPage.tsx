import { useState, useCallback, useRef } from 'react'
import { Link } from 'react-router-dom'
import { Upload, FileText, History, Loader2, CheckCircle2, AlertCircle } from 'lucide-react'
import { useAuth } from '../../auth/useAuth'
import { ingestContent } from '../../api/ingestor-api'
import { parseTarGz } from '../../utils/targz-parser'

export default function UploadPage() {
  const { getAccessToken } = useAuth()
  const fileInputRef = useRef<HTMLInputElement>(null)

  const [uploading, setUploading] = useState(false)
  const [result, setResult] = useState<{ nodeIds: string[]; edgeIds: string[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ nodes: number; edges: number; texFiles: number } | null>(null)

  const handleFileSelect = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    setFileName(file.name)
    setResult(null)
    setError(null)
    setPreview(null)

    try {
      const buffer = await file.arrayBuffer()

      if (file.name.endsWith('.tar.gz') || file.name.endsWith('.tgz')) {
        const parsed = parseTarGz(buffer)
        setPreview({
          nodes: parsed.nodes.length,
          edges: parsed.edges.length,
          texFiles: parsed.texContent.length,
        })
      } else if (file.name.endsWith('.json')) {
        const text = new TextDecoder().decode(buffer)
        const data = JSON.parse(text)
        setPreview({
          nodes: (data.nodes ?? []).length,
          edges: (data.edges ?? []).length,
          texFiles: 0,
        })
      } else {
        setError('Unsupported file type. Use .tar.gz or .json')
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to parse file')
    }
  }, [])

  const handleUpload = useCallback(async () => {
    const file = fileInputRef.current?.files?.[0]
    if (!file) return

    const token = getAccessToken()
    if (!token) return

    setUploading(true)
    setError(null)
    setResult(null)

    try {
      const buffer = await file.arrayBuffer()
      let nodes: Array<{ type: string; properties: Record<string, unknown> }> = []
      let edges: Array<{ source: string; target: string; type: string; properties?: Record<string, unknown> }> = []

      if (file.name.endsWith('.tar.gz') || file.name.endsWith('.tgz')) {
        const parsed = parseTarGz(buffer)
        nodes = parsed.nodes.map((n) => ({ type: n.type, properties: n.properties }))
        edges = parsed.edges.map((e) => ({ source: e.source, target: e.target, type: e.type, properties: e.properties }))
      } else {
        const text = new TextDecoder().decode(buffer)
        const data = JSON.parse(text)
        nodes = data.nodes ?? []
        edges = data.edges ?? []
      }

      const res = await ingestContent({ nodes, edges }, token)
      setResult(res)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed')
    } finally {
      setUploading(false)
    }
  }, [getAccessToken])

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        <div className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
              Upload Knowledge
            </h1>
            <p className="text-xs mt-1" style={{ color: 'var(--text-dimmed)' }}>
              Ingest knowledge content into the graph as raw (red) nodes
            </p>
          </div>
          <Link to="/upload/history" className="btn-secondary text-xs gap-1.5 py-1.5 px-3">
            <History size={14} />
            History
          </Link>
        </div>

        {/* Drop zone */}
        <div
          className="card flex flex-col items-center justify-center py-12 px-8 cursor-pointer transition-all"
          style={{ borderStyle: 'dashed', borderWidth: 2 }}
          onClick={() => fileInputRef.current?.click()}
        >
          <Upload size={32} style={{ color: 'var(--text-dimmed)' }} className="mb-3" />
          <p className="text-sm font-medium" style={{ color: 'var(--text-secondary)' }}>
            {fileName ?? 'Click to select a file'}
          </p>
          <p className="text-[11px] mt-1" style={{ color: 'var(--text-dimmed)' }}>
            Supported formats: .tar.gz, .tgz, .json
          </p>
          <input
            ref={fileInputRef}
            type="file"
            accept=".tar.gz,.tgz,.json"
            onChange={handleFileSelect}
            className="hidden"
          />
        </div>

        {/* Preview */}
        {preview && (
          <div className="card mt-4 px-4 py-3">
            <h3 className="text-[10px] font-semibold uppercase tracking-wider mb-2" style={{ color: 'var(--text-dimmed)' }}>
              Preview
            </h3>
            <div className="flex items-center gap-4">
              <div className="flex items-center gap-1.5">
                <FileText size={14} style={{ color: 'var(--accent-blue)' }} />
                <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>{preview.nodes} nodes</span>
              </div>
              <div className="flex items-center gap-1.5">
                <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>{preview.edges} edges</span>
              </div>
              {preview.texFiles > 0 && (
                <div className="flex items-center gap-1.5">
                  <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>{preview.texFiles} TeX files</span>
                </div>
              )}
            </div>
            <button
              onClick={handleUpload}
              disabled={uploading}
              className="btn-neon-green text-xs gap-1.5 py-1.5 px-4 mt-3 disabled:opacity-50"
            >
              {uploading ? <Loader2 size={14} className="animate-spin" /> : <Upload size={14} />}
              Upload to Graph
            </button>
          </div>
        )}

        {/* Result */}
        {result && (
          <div className="card mt-4 px-4 py-3" style={{ borderColor: 'rgba(134,239,172,0.3)' }}>
            <div className="flex items-center gap-2 mb-2">
              <CheckCircle2 size={16} style={{ color: 'var(--neon-green)' }} />
              <span className="text-sm font-medium" style={{ color: 'var(--neon-green)' }}>Upload successful</span>
            </div>
            <div className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              Created {result.nodeIds.length} nodes and {result.edgeIds.length} edges
            </div>
          </div>
        )}

        {/* Error */}
        {error && (
          <div className="card mt-4 px-4 py-3" style={{ borderColor: 'rgba(252,165,165,0.3)' }}>
            <div className="flex items-center gap-2">
              <AlertCircle size={16} style={{ color: 'var(--accent-red)' }} />
              <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{error}</span>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
