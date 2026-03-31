import { Loader2, Trash2 } from 'lucide-react';
import type { ConfigStore } from '../useConfigStore';

type Props = {
  store: ConfigStore;
  conversationId: string;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function ChatHistoryViewer({ store, conversationId, flash }: Props) {
  const meta = store.chatConversations.find(c => c.id === conversationId);

  async function handleDelete() {
    try {
      await store.deleteChatConversation(conversationId);
      flash('Conversation deleted', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to delete', 'error');
    }
  }

  if (store.chatLoading) {
    return (
      <div className="py-12 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin" />
        <span>Loading conversation...</span>
      </div>
    );
  }

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">chat-histories/</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">{meta?.title || conversationId}</div>
          {meta && (
            <div className="text-[11px] text-gray-400 mt-1">
              {meta.messageCount} messages · {meta.serviceKind} · {new Date(meta.updatedAt).toLocaleString()}
            </div>
          )}
        </div>
        <button
          onClick={handleDelete}
          className="inline-flex items-center gap-1.5 rounded-lg border border-red-200 px-3 py-2 text-[12px] font-medium text-red-600 hover:bg-red-50 transition-colors"
        >
          <Trash2 size={13} />
          Delete
        </button>
      </div>

      {/* Messages */}
      <div className="space-y-3">
        {store.selectedConversationMessages.length === 0 ? (
          <div className="rounded-2xl border border-[#EEEAE4] bg-white p-6 text-center text-[13px] text-gray-400">
            No messages in this conversation
          </div>
        ) : (
          store.selectedConversationMessages.map(msg => (
            <div
              key={msg.id}
              className={`rounded-2xl border p-4 text-[13px] leading-relaxed ${
                msg.role === 'user'
                  ? 'border-blue-100 bg-blue-50/50'
                  : 'border-[#EEEAE4] bg-white'
              }`}
            >
              <div className="flex items-center justify-between mb-2">
                <span className={`text-[10px] font-semibold uppercase tracking-wider ${
                  msg.role === 'user' ? 'text-blue-500' : 'text-gray-400'
                }`}>
                  {msg.role}
                </span>
                <span className="text-[10px] text-gray-400">
                  {new Date(msg.timestamp).toLocaleString()}
                </span>
              </div>
              {msg.thinking && (
                <div className="mb-2 text-[12px] text-gray-400 italic border-l-2 border-gray-200 pl-3">
                  {msg.thinking}
                </div>
              )}
              <div className="whitespace-pre-wrap break-words">{msg.content}</div>
              {msg.error && (
                <div className="mt-2 text-[12px] text-red-500">{msg.error}</div>
              )}
            </div>
          ))
        )}
      </div>
    </div>
  );
}
