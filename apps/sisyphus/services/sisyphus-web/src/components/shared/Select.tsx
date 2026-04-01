import { useState, useRef, useEffect, useCallback } from 'react'
import { createPortal } from 'react-dom'
import { ChevronDown, Check } from 'lucide-react'

interface SelectOption {
  value: string
  label: string
}

interface SelectProps {
  value: string
  options: SelectOption[] | string[]
  onChange: (value: string) => void
  placeholder?: string
  className?: string
}

export default function Select({ value, options, onChange, placeholder = 'Select...', className = '' }: SelectProps) {
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const dropdownRef = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState<{ top: number; left: number; width: number; direction: 'down' | 'up' }>({ top: 0, left: 0, width: 0, direction: 'down' })

  const normalized: SelectOption[] = options.map((o) =>
    typeof o === 'string' ? { value: o, label: o || '(none)' } : o,
  )

  const selected = normalized.find((o) => o.value === value)

  // Position dropdown relative to trigger
  useEffect(() => {
    if (!open || !triggerRef.current) return
    const rect = triggerRef.current.getBoundingClientRect()
    const spaceBelow = window.innerHeight - rect.bottom
    const direction = spaceBelow < 200 ? 'up' : 'down'
    setPos({
      top: direction === 'down' ? rect.bottom + 4 : rect.top - 4,
      left: rect.left,
      width: rect.width,
      direction,
    })
  }, [open])

  // Close on outside click
  useEffect(() => {
    if (!open) return
    const handler = (e: MouseEvent) => {
      const target = e.target as Node
      if (triggerRef.current?.contains(target)) return
      if (dropdownRef.current?.contains(target)) return
      setOpen(false)
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [open])

  useEffect(() => {
    if (!open) return
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('keydown', handler)
    return () => document.removeEventListener('keydown', handler)
  }, [open])

  const handleSelect = useCallback((val: string) => {
    onChange(val)
    setOpen(false)
  }, [onChange])

  const dropdown = open ? createPortal(
    <div
      ref={dropdownRef}
      className="fixed z-[9999] rounded-lg overflow-hidden animate-scale-in"
      style={{
        top: pos.direction === 'down' ? pos.top : undefined,
        bottom: pos.direction === 'up' ? window.innerHeight - pos.top : undefined,
        left: pos.left,
        width: pos.width,
        background: 'var(--bg-surface)',
        border: '1px solid var(--border-default)',
        boxShadow: 'var(--shadow-lg)',
        maxHeight: 200,
        overflowY: 'auto',
      }}
    >
      {normalized.map((opt) => {
        const isActive = opt.value === value
        return (
          <button
            key={opt.value}
            type="button"
            onClick={() => handleSelect(opt.value)}
            className="w-full flex items-center gap-2 px-3 py-2 text-sm text-left transition-colors"
            style={{
              color: isActive ? 'var(--neon-cyan)' : 'var(--text-secondary)',
              background: isActive ? 'rgba(125,211,252,0.08)' : 'transparent',
            }}
            onMouseEnter={(e) => { if (!isActive) e.currentTarget.style.background = 'var(--bg-elevated)' }}
            onMouseLeave={(e) => { if (!isActive) e.currentTarget.style.background = isActive ? 'rgba(125,211,252,0.08)' : 'transparent' }}
          >
            <span className="w-4 shrink-0">
              {isActive && <Check size={13} style={{ color: 'var(--neon-cyan)' }} />}
            </span>
            <span className="font-mono text-xs">{opt.label}</span>
          </button>
        )
      })}
    </div>,
    document.body,
  ) : null

  return (
    <div className={`relative ${className}`}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen(!open)}
        className="input flex items-center justify-between gap-2 text-left cursor-pointer"
      >
        <span style={{ color: selected?.value ? 'var(--text-primary)' : 'var(--text-dimmed)' }}>
          {selected?.label ?? placeholder}
        </span>
        <ChevronDown size={14} style={{ color: 'var(--text-dimmed)', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>
      {dropdown}
    </div>
  )
}
