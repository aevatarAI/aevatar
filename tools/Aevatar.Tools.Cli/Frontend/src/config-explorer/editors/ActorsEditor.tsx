import { useState } from 'react';
import { Trash2, Bot, ChevronDown, ChevronRight } from 'lucide-react';
import type { ConfigStore } from '../useConfigStore';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function ActorsEditor({ store, flash }: Props) {
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());

  function toggleGroup(type: string) {
    setExpandedGroups(prev => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type); else next.add(type);
      return next;
    });
  }

  async function handleRemove(gagentType: string, actorId: string) {
    try {
      await store.removeActor(gagentType, actorId);
      flash('Actor removed', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to remove actor', 'error');
    }
  }

  const totalActors = store.actorGroups.reduce((n, g) => n + g.actorIds.length, 0);

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div>
        <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">actors.json</div>
        <div className="text-[16px] font-bold text-gray-800 mt-0.5">GAgent Actors</div>
        <div className="text-[12px] text-gray-400 mt-1">Actors are created automatically when you invoke a service. Use this view to inspect or remove them.</div>
      </div>

      {/* Actor groups */}
      {store.actorGroups.length === 0 ? (
        <div className="rounded-2xl border border-dashed border-[#E6E3DE] bg-white p-8 text-center">
          <Bot size={32} className="mx-auto text-gray-300 mb-2" />
          <div className="text-[13px] text-gray-400">No actors yet</div>
          <div className="text-[11px] text-gray-300 mt-1">Invoke a GAgent service to create actors</div>
        </div>
      ) : (
        <div className="space-y-2">
          <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400 px-1">
            {totalActors} actor{totalActors !== 1 ? 's' : ''} in {store.actorGroups.length} group{store.actorGroups.length !== 1 ? 's' : ''}
          </div>
          {store.actorGroups.map(group => {
            const expanded = expandedGroups.has(group.gAgentType);
            const shortType = group.gAgentType.split('.').pop() || group.gAgentType;
            return (
              <div key={group.gAgentType} className="rounded-2xl border border-[#EEEAE4] bg-white overflow-hidden">
                <button
                  onClick={() => toggleGroup(group.gAgentType)}
                  className="w-full flex items-center gap-2.5 px-5 py-3 text-left hover:bg-gray-50 transition-colors"
                >
                  {expanded ? <ChevronDown size={14} className="text-gray-400" /> : <ChevronRight size={14} className="text-gray-400" />}
                  <Bot size={14} className="text-orange-500 flex-shrink-0" />
                  <span className="text-[13px] font-semibold text-gray-800 flex-1">{shortType}</span>
                  <span className="text-[11px] text-gray-400 bg-gray-100 rounded-full px-2 py-0.5">{group.actorIds.length}</span>
                </button>
                {expanded && (
                  <div className="px-5 pb-4 space-y-1.5 border-t border-[#F0EDE8]">
                    <div className="text-[10px] text-gray-400 font-mono pt-2 truncate" title={group.gAgentType}>{group.gAgentType}</div>
                    {group.actorIds.map(id => (
                      <div key={id} className="flex items-center gap-2 group">
                        <span className="flex-1 text-[12px] font-mono text-gray-600 truncate bg-gray-50 rounded-lg px-3 py-1.5" title={id}>
                          {id}
                        </span>
                        <button
                          onClick={() => handleRemove(group.gAgentType, id)}
                          className="opacity-0 group-hover:opacity-100 rounded p-1 text-gray-400 hover:text-red-500 hover:bg-red-50 transition-all"
                          title="Remove"
                        >
                          <Trash2 size={13} />
                        </button>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
