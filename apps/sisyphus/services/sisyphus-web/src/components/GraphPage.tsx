import { useState, useCallback, useRef } from 'react'
import { useLocation } from 'react-router-dom'
import { Layers } from 'lucide-react'
import GraphView from './GraphView'
import CompilePopup from './CompilePopup'
import type { GraphSnapshot } from '../types/graph'

export default function GraphPage() {
  const location = useLocation()
  const isActive = location.pathname === '/graph' || location.pathname === '/'
  const [showCompile, setShowCompile] = useState(false)
  const filteredSnapshotRef = useRef<GraphSnapshot | null>(null)
  const [filterName, setFilterName] = useState('all')

  const handleFilteredSnapshotChange = useCallback((snapshot: GraphSnapshot | null) => {
    filteredSnapshotRef.current = snapshot
  }, [])

  const handleFilterNameChange = useCallback((name: string) => {
    setFilterName(name)
  }, [])

  return (
    <div className="h-full w-full overflow-hidden relative">
      <div className="absolute inset-0">
        <GraphView
          onFilteredSnapshotChange={handleFilteredSnapshotChange}
          onFilterNameChange={handleFilterNameChange}
        />
      </div>

      {isActive && (
        <div className="absolute top-3 left-1/2 -translate-x-1/2 z-20 flex items-center gap-2">
          <button
            onClick={() => setShowCompile(true)}
            className="btn-neon-blue text-xs gap-1.5 py-1.5 px-3"
          >
            <Layers size={14} />
            Compile Current Filter
          </button>
        </div>
      )}

      {showCompile && (
        <CompilePopup
          snapshot={filteredSnapshotRef.current}
          filterName={filterName}
          onClose={() => setShowCompile(false)}
        />
      )}
    </div>
  )
}
