import { useRef, useState, useMemo, useCallback, useEffect } from 'react'
import ForceGraph3D from 'react-force-graph-3d'
import * as THREE from 'three'
import { EffectComposer } from 'three/examples/jsm/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/examples/jsm/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/examples/jsm/postprocessing/UnrealBloomPass.js'
import { RefreshCw } from 'lucide-react'
import { useGraphData } from '../hooks/use-graph-data'
import { getNodeColor, getEdgeColor, buildLegend } from '../types/graph'
import type { GraphSnapshot } from '../types/graph'
import NodeDetailsPanel from './NodeDetailsPanel'

// Shared geometry + glow texture (reused across all nodes)
const _sphereGeo = new THREE.SphereGeometry(1, 16, 16)
const _glowTex = (() => {
  const size = 128
  const canvas = document.createElement('canvas')
  canvas.width = size; canvas.height = size
  const ctx = canvas.getContext('2d')!
  const g = ctx.createRadialGradient(size/2, size/2, 0, size/2, size/2, size/2)
  g.addColorStop(0, 'rgba(255,255,255,0.8)')
  g.addColorStop(0.2, 'rgba(255,255,255,0.4)')
  g.addColorStop(0.5, 'rgba(255,255,255,0.1)')
  g.addColorStop(1, 'rgba(255,255,255,0)')
  ctx.fillStyle = g; ctx.fillRect(0, 0, size, size)
  return new THREE.CanvasTexture(canvas)
})()

interface GraphViewProps {
  onFilteredSnapshotChange?: (snapshot: GraphSnapshot | null) => void
  onFilterNameChange?: (name: string) => void
}

export default function GraphView({ onFilteredSnapshotChange, onFilterNameChange }: GraphViewProps) {
  const {
    snapshot, fullSnapshot, selectedNode, traverseResult, loading, error, refresh, selectNode, clearSelection,
    filters, setFilters, filterOptions,
  } = useGraphData()
  const fgRef = useRef<any>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  const [dimensions, setDimensions] = useState({ width: window.innerWidth, height: window.innerHeight })
  const [legendCollapsed, setLegendCollapsed] = useState(false)
  const [rendering, setRendering] = useState(false)
  const prevSnapshotRef = useRef(snapshot)

  // Show rendering overlay when snapshot changes (filter switch)
  // Stays visible until ForceGraph3D finishes warmup via onEngineStop
  useEffect(() => {
    if (snapshot !== prevSnapshotRef.current && snapshot && snapshot.nodes.length > 0) {
      setRendering(true)
    }
    prevSnapshotRef.current = snapshot
  }, [snapshot])

  // Notify parent of filtered snapshot changes
  useEffect(() => {
    onFilteredSnapshotChange?.(snapshot)
  }, [snapshot, onFilteredSnapshotChange])

  // Notify parent of filter name for compile file naming
  useEffect(() => {
    const parts = [filters.layer || 'all', filters.type, filters.edgeType].filter(Boolean)
    onFilterNameChange?.(parts.join('_') || 'all')
  }, [filters, onFilterNameChange])

  // Track container dimensions
  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        setDimensions({ width: entry.contentRect.width, height: entry.contentRect.height })
      }
    })
    observer.observe(container)
    return () => observer.disconnect()
  }, [])

  // Tune forces for large graphs
  useEffect(() => {
    const fg = fgRef.current
    if (!fg) return
    const n = snapshot?.nodes.length ?? 0
    if (n > 5000) {
      fg.d3Force('charge')?.strength(-5)
      fg.d3Force('link')?.distance(15)
    } else if (n > 1000) {
      fg.d3Force('charge')?.strength(-15)
      fg.d3Force('link')?.distance(25)
    } else {
      fg.d3Force('charge')?.strength(-30)
      fg.d3Force('link')?.distance(40)
    }
  }, [snapshot])

  const graphData = useMemo(() => {
    if (!snapshot) return { nodes: [], links: [] }
    return {
      nodes: snapshot.nodes.map((n) => ({ ...n })),
      links: snapshot.edges.map((e) => ({ source: e.source, target: e.target, type: e.type, id: e.id })),
    }
  }, [snapshot])

  // Legend data — from fullSnapshot so all types are always visible
  const legend = useMemo(() => {
    if (!fullSnapshot) return { nodeColors: [], edgeColors: [] }
    return buildLegend(fullSnapshot)
  }, [fullSnapshot])

  // Custom 3D node with glowing sphere + outer glow sprite
  const nodeThreeObject = useCallback((node: any) => {
    const color = new THREE.Color(getNodeColor(node))
    const group = new THREE.Group()

    // Glass-like sphere with emissive glow
    const mat = new THREE.MeshPhysicalMaterial({
      color,
      emissive: color,
      emissiveIntensity: 0.8,
      metalness: 0.1,
      roughness: 0.15,
      clearcoat: 1.0,
      clearcoatRoughness: 0.1,
      transparent: true,
      opacity: 0.92,
    })
    const sphere = new THREE.Mesh(_sphereGeo, mat)
    sphere.scale.setScalar(1.8)
    group.add(sphere)

    // Soft outer glow
    const spriteMat = new THREE.SpriteMaterial({
      map: _glowTex,
      color,
      transparent: true,
      opacity: 0.35,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    })
    const glow = new THREE.Sprite(spriteMat)
    glow.scale.set(12, 12, 1)
    group.add(glow)

    return group
  }, [])

  // Setup bloom post-processing after graph mounts
  useEffect(() => {
    const fg = fgRef.current
    if (!fg) return
    const renderer = fg.renderer() as THREE.WebGLRenderer
    const scene = fg.scene() as THREE.Scene
    const camera = fg.camera() as THREE.Camera
    if (!renderer || !scene || !camera) return

    const composer = new EffectComposer(renderer)
    composer.addPass(new RenderPass(scene, camera))
    const bloom = new UnrealBloomPass(
      new THREE.Vector2(dimensions.width, dimensions.height),
      0.8,   // strength
      0.4,   // radius
      0.6,   // threshold
    )
    composer.addPass(bloom)

    // Override the render loop
    const origAnimate = fg._animationCycle
    fg._animationCycle = () => {
      origAnimate?.call(fg)
      composer.render()
    }

    return () => { fg._animationCycle = origAnimate }
  }, [snapshot, rendering, dimensions])

  const handleNodeClick = useCallback((node: any) => {
    selectNode(node.id)
  }, [selectNode])

  const handleBackgroundClick = useCallback(() => {
    clearSelection()
  }, [clearSelection])

  const isEmpty = snapshot && snapshot.nodes.length === 0

  // Loading state
  if (loading && (!snapshot || snapshot.nodes.length === 0)) {
    return (
      <div ref={containerRef} className="h-full w-full flex flex-col items-center justify-center" style={{ background: '#050508' }}>
        <div className="animate-spin mb-4" style={{ color: 'var(--neon-cyan)' }}>
          <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M21 12a9 9 0 1 1-6.219-8.56" />
          </svg>
        </div>
        <span className="text-sm font-medium" style={{ color: 'var(--neon-cyan)', textShadow: '0 0 10px rgba(0,212,255,0.3)' }}>
          Loading graph snapshot...
        </span>
        <span className="text-[10px] mt-1" style={{ color: 'var(--text-dimmed)' }}>
          This may take a few seconds for large graphs
        </span>
      </div>
    )
  }

  // Error state
  if (error && !snapshot) {
    return (
      <div className="h-full w-full flex items-center justify-center" style={{ background: '#050508' }}>
        <p className="text-xs" style={{ color: 'var(--accent-red)' }}>{error}</p>
      </div>
    )
  }

  return (
    <div ref={containerRef} className="h-full w-full relative overflow-hidden" style={{ background: '#050508' }}>
      {/* Empty state message */}
      {isEmpty && (
        <div className="absolute inset-0 flex items-center justify-center z-0">
          <p className="text-xs" style={{ color: 'var(--text-dimmed)' }}>
            No graph data matches the current filters.
          </p>
        </div>
      )}

      {/* Rendering overlay */}
      {rendering && (
        <div className="absolute inset-0 z-30 flex flex-col items-center justify-center" style={{ background: 'rgba(5,5,8,0.9)' }}>
          <div className="animate-spin mb-4" style={{ color: 'var(--neon-cyan)' }}>
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
          </div>
          <span className="text-sm font-medium" style={{ color: 'var(--neon-cyan)', textShadow: '0 0 10px rgba(0,212,255,0.3)' }}>
            Rendering {snapshot?.nodes.length ?? 0} nodes...
          </span>
        </div>
      )}

      {/* 3D Force Graph — key forces remount on filter change so onEngineStop fires */}
      {snapshot && !isEmpty && (
        <ForceGraph3D
          key={`${filters.layer}_${filters.type}_${filters.edgeType}_${filters.search}`}
          ref={fgRef}
          width={dimensions.width}
          height={dimensions.height}
          graphData={graphData}
          backgroundColor="#050508"
          nodeThreeObject={nodeThreeObject}
          nodeThreeObjectExtend={false}
          linkColor={(link: any) => getEdgeColor(link)}
          linkOpacity={0.6}
          linkWidth={0.4}
          linkDirectionalParticles={2}
          linkDirectionalParticleWidth={0.6}
          linkDirectionalParticleSpeed={0.004}
          linkDirectionalParticleColor={(link: any) => getEdgeColor(link)}
          onNodeClick={handleNodeClick}
          onBackgroundClick={handleBackgroundClick}
          enableNodeDrag={false}
          warmupTicks={100}
          cooldownTicks={0}
          d3AlphaDecay={0.05}
          d3VelocityDecay={0.4}
          onEngineStop={() => setRendering(false)}
        />
      )}

      {/* Filter bar (top-left) */}
      <div className="absolute top-3 left-3 flex items-center gap-2 z-10">
        <input
          type="text"
          placeholder="Search nodes..."
          value={filters.search}
          onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))}
          className="text-[11px] px-2 py-1.5 rounded font-mono"
          style={{ background: 'rgba(10, 10, 12, 0.85)', border: '1px solid rgba(125, 211, 252, 0.15)', color: '#ccc', width: 160, outline: 'none' }}
        />
        <select
          value={filters.layer}
          onChange={(e) => setFilters((f) => ({ ...f, layer: e.target.value, type: '' }))}
          className="text-[11px] px-2 py-1.5 rounded font-mono"
          style={{ background: 'rgba(10, 10, 12, 0.85)', border: '1px solid rgba(125, 211, 252, 0.15)', color: '#ccc', outline: 'none' }}
        >
          <option value="">All layers</option>
          <option value="raw">Raw</option>
          <option value="purified">Purified</option>
          <option value="verified">Verified</option>
        </select>
        <select
          value={filters.type}
          onChange={(e) => setFilters((f) => ({ ...f, type: e.target.value }))}
          className="text-[11px] px-2 py-1.5 rounded font-mono"
          style={{ background: 'rgba(10, 10, 12, 0.85)', border: '1px solid rgba(125, 211, 252, 0.15)', color: '#ccc', outline: 'none' }}
        >
          <option value="">All types</option>
          {filterOptions.types
            .filter((t) => !filters.layer || t === filters.layer || t.startsWith(filters.layer + '_'))
            .map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <select
          value={filters.edgeType}
          onChange={(e) => setFilters((f) => ({ ...f, edgeType: e.target.value }))}
          className="text-[11px] px-2 py-1.5 rounded font-mono"
          style={{ background: 'rgba(10, 10, 12, 0.85)', border: '1px solid rgba(125, 211, 252, 0.15)', color: '#ccc', outline: 'none' }}
        >
          <option value="">All edges</option>
          {filterOptions.edgeTypes.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        {(filters.type || filters.layer || filters.edgeType || filters.search) && (
          <button
            onClick={() => setFilters({ type: '', layer: '', edgeType: '', search: '' })}
            className="text-[10px] px-2 py-1 rounded"
            style={{ background: 'rgba(255,68,68,0.15)', border: '1px solid rgba(255,68,68,0.3)', color: 'var(--neon-red)' }}
          >
            Clear
          </button>
        )}
      </div>

      {/* Toolbar (top-right) */}
      <div className="absolute top-3 right-3 flex items-center gap-1 z-10">
        <button onClick={refresh} className="icon-btn" title="Refresh graph"
          style={{ background: 'rgba(10, 10, 12, 0.8)', border: '1px solid rgba(125, 211, 252, 0.15)' }}>
          <RefreshCw size={14} className={loading ? 'animate-spin' : ''} style={{ color: 'var(--neon-cyan)' }} />
        </button>
      </div>

      {/* Legend (bottom-left) — always shown when data loaded */}
      {fullSnapshot && (legend.nodeColors.length > 0 || legend.edgeColors.length > 0) && (
        <div
          className="absolute bottom-3 left-3 z-10 rounded overflow-hidden"
          style={{
            background: 'rgba(10, 10, 12, 0.88)',
            border: '1px solid rgba(125, 211, 252, 0.12)',
            maxHeight: legendCollapsed ? 28 : 320,
            minWidth: 160,
            transition: 'max-height 0.2s ease',
          }}
        >
          <button
            onClick={() => setLegendCollapsed((c) => !c)}
            className="w-full flex items-center justify-between px-2.5 py-1.5 text-[10px] font-semibold uppercase tracking-wider"
            style={{ color: 'var(--neon-cyan)', borderBottom: legendCollapsed ? 'none' : '1px solid rgba(125,211,252,0.1)' }}
          >
            <span>Legend — {snapshot?.nodes.length ?? 0}n / {snapshot?.edges.length ?? 0}e</span>
            <span>{legendCollapsed ? '▸' : '▾'}</span>
          </button>
          {!legendCollapsed && (
            <div className="px-2.5 py-2 space-y-2 overflow-y-auto" style={{ maxHeight: 280 }}>
              {legend.nodeColors.length > 0 && (
                <div>
                  <div className="text-[9px] font-semibold uppercase tracking-wider mb-1" style={{ color: 'var(--text-dimmed)' }}>Nodes</div>
                  <div className="space-y-0.5">
                    {legend.nodeColors.map(([type, color]) => (
                      <button key={type} onClick={() => setFilters((f) => ({ ...f, type: f.type === type ? '' : type }))}
                        className="flex items-center gap-1.5 w-full text-left rounded px-1 py-0.5 hover:opacity-80"
                        style={{ background: filters.type === type ? 'rgba(125,211,252,0.1)' : 'transparent' }}>
                        <div className="w-2.5 h-2.5 rounded-full shrink-0" style={{ background: color, boxShadow: `0 0 4px ${color}` }} />
                        <span className="text-[10px] font-mono truncate" style={{ color: '#ccc' }}>{type}</span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
              {legend.edgeColors.length > 0 && (
                <div>
                  <div className="text-[9px] font-semibold uppercase tracking-wider mb-1" style={{ color: 'var(--text-dimmed)' }}>Edges</div>
                  <div className="space-y-0.5">
                    {legend.edgeColors.map(([type, color]) => (
                      <button key={type} onClick={() => setFilters((f) => ({ ...f, edgeType: f.edgeType === type ? '' : type }))}
                        className="flex items-center gap-1.5 w-full text-left rounded px-1 py-0.5 hover:opacity-80"
                        style={{ background: filters.edgeType === type ? 'rgba(125,211,252,0.1)' : 'transparent' }}>
                        <div className="w-4 h-0.5 shrink-0 rounded" style={{ background: color, boxShadow: `0 0 4px ${color}` }} />
                        <span className="text-[10px] font-mono truncate" style={{ color: '#ccc' }}>{type}</span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {/* Node detail panel */}
      {selectedNode && (
        <NodeDetailsPanel
          node={selectedNode}
          traverseResult={traverseResult}
          onClose={clearSelection}
        />
      )}
    </div>
  )
}
