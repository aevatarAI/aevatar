import { useCallback, useEffect, useRef, useState } from 'react';
import { normalizeBackendSseFrame, type RuntimeEvent } from './sseUtils';
import type { ChatMessage, ServiceOption } from './chatTypes';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';

// ── Helpers ─────────────────────────────────────────────────────────────────────

function genId() {
  return crypto.randomUUID?.() ?? Math.random().toString(36).slice(2);
}

// ── Chat Message Bubble ─────────────────────────────────────────────────────────

function ChatBubble({ msg }: { msg: ChatMessage }) {
  const isUser = msg.role === 'user';
  return (
    <div className={`flex gap-3 ${isUser ? 'flex-row-reverse' : ''}`}>
      {/* Avatar */}
      <div className={`flex-shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[12px] font-bold ${
        isUser ? 'bg-blue-100 text-blue-600' : 'bg-purple-100 text-purple-600'
      }`}>
        {isUser ? 'U' : 'A'}
      </div>
      {/* Content */}
      <div className={`max-w-[75%] min-w-[60px] rounded-2xl px-4 py-3 text-[14px] leading-relaxed ${
        isUser
          ? 'bg-blue-600 text-white rounded-br-md'
          : 'bg-white border border-[#E6E3DE] text-gray-800 rounded-bl-md'
      }`}>
        <div className="whitespace-pre-wrap break-words">{msg.content || '\u00a0'}</div>
        {msg.status === 'streaming' && (
          <span className="inline-block w-1.5 h-4 bg-current opacity-60 animate-pulse ml-0.5 align-text-bottom" />
        )}
        {msg.status === 'error' && msg.error && (
          <div className="mt-2 text-[12px] text-red-400 border-t border-red-200/30 pt-1">{msg.error}</div>
        )}
      </div>
    </div>
  );
}

// ── Service Selector ────────────────────────────────────────────────────────────

function ServiceSelector({
  services,
  selected,
  onSelect,
}: {
  services: ServiceOption[];
  selected: string;
  onSelect: (id: string) => void;
}) {
  return (
    <select
      value={selected}
      onChange={e => onSelect(e.target.value)}
      className="rounded-lg border border-[#E6E3DE] bg-white px-3 py-1.5 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
    >
      {services.map(s => (
        <option key={s.id} value={s.id}>{s.label}</option>
      ))}
    </select>
  );
}

// ── Chat Input ──────────────────────────────────────────────────────────────────

function ChatInput({
  onSend,
  onStop,
  isStreaming,
  disabled,
}: {
  onSend: (text: string) => void;
  onStop: () => void;
  isStreaming: boolean;
  disabled: boolean;
}) {
  const [text, setText] = useState('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const handleSend = useCallback(() => {
    const trimmed = text.trim();
    if (!trimmed || isStreaming || disabled) return;
    onSend(trimmed);
    setText('');
    if (textareaRef.current) textareaRef.current.style.height = 'auto';
  }, [text, isStreaming, disabled, onSend]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const handleInput = () => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 160) + 'px';
  };

  return (
    <div className="flex items-end gap-2">
      <textarea
        ref={textareaRef}
        rows={1}
        className="flex-1 resize-none rounded-xl border border-[#E6E3DE] bg-white px-4 py-3 text-[14px] focus:outline-none focus:ring-2 focus:ring-blue-400 placeholder:text-gray-400"
        value={text}
        onChange={e => { setText(e.target.value); handleInput(); }}
        onKeyDown={handleKeyDown}
        placeholder="Send a message..."
        disabled={disabled}
      />
      {isStreaming ? (
        <button
          onClick={onStop}
          className="flex-shrink-0 rounded-xl bg-red-500 px-4 py-3 text-white text-[14px] font-semibold hover:bg-red-600"
        >
          Stop
        </button>
      ) : (
        <button
          onClick={handleSend}
          disabled={!text.trim() || disabled}
          className="flex-shrink-0 rounded-xl bg-[#18181B] px-4 py-3 text-white text-[14px] font-semibold hover:bg-[#333] disabled:opacity-30"
        >
          Send
        </button>
      )}
    </div>
  );
}

// ── Debug Panel (extracted from old ScopePage) ──────────────────────────────────

function DebugPanel({ events }: { events: RuntimeEvent[] }) {
  if (events.length === 0) return null;
  return (
    <div className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden max-h-[300px] overflow-auto">
      <div className="px-4 py-2 border-b border-[#E6E3DE] text-[11px] font-semibold uppercase tracking-wider text-gray-400">
        Events ({events.length})
      </div>
      <div className="divide-y divide-[#F0EDE8]">
        {events.map((evt, i) => (
          <div key={i} className="px-4 py-1.5 text-[11px] font-mono text-gray-600 flex gap-2">
            <span className="text-gray-300 w-4 text-right flex-shrink-0">{i + 1}</span>
            <span className="font-semibold text-gray-500">{evt.type}</span>
            {evt.type === 'TEXT_MESSAGE_CONTENT' && (
              <span className="text-gray-400 truncate">{String(evt.delta || '').slice(0, 60)}</span>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Main ScopePage ──────────────────────────────────────────────────────────────

export default function ScopePage() {
  const scopeId = nyxid.loadSession()?.user.sub || '';

  // Services
  const [services, setServices] = useState<ServiceOption[]>([
    { id: 'nyxid-chat', label: 'NyxID Chat', kind: 'nyxid-chat' },
  ]);
  const [selectedService, setSelectedService] = useState('nyxid-chat');

  // Conversations
  const [currentActorId, setCurrentActorId] = useState('');

  // Chat
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [debugEvents, setDebugEvents] = useState<RuntimeEvent[]>([]);
  const [showDebug, setShowDebug] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Load services on mount
  useEffect(() => {
    if (!scopeId) return;
    api.scope.listServices(scopeId).then(svcList => {
      const base: ServiceOption[] = [
        { id: 'nyxid-chat', label: 'NyxID Chat', kind: 'nyxid-chat' },
      ];
      if (Array.isArray(svcList)) {
        for (const s of svcList) {
          const sid = s?.serviceId || s?.ServiceId;
          const name = s?.displayName || s?.DisplayName || sid;
          if (sid) base.push({ id: sid, label: name, kind: 'service' });
        }
      }
      setServices(base);
    }).catch(() => {});
  }, [scopeId]);

  // Auto-scroll
  useEffect(() => {
    scrollRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Create conversation for NyxID Chat
  const ensureConversation = useCallback(async () => {
    if (currentActorId) return currentActorId;
    const result = await api.nyxidChat.createConversation(scopeId);
    setCurrentActorId(result.actorId);
    return result.actorId;
  }, [scopeId, currentActorId]);

  // Send message
  const handleSend = useCallback(async (text: string) => {
    if (!scopeId || isStreaming) return;

    const userMsg: ChatMessage = {
      id: genId(),
      role: 'user',
      content: text,
      timestamp: Date.now(),
      status: 'complete',
    };
    const assistantMsg: ChatMessage = {
      id: genId(),
      role: 'assistant',
      content: '',
      timestamp: Date.now(),
      status: 'streaming',
    };

    setMessages(prev => [...prev, userMsg, assistantMsg]);
    setIsStreaming(true);
    setDebugEvents([]);

    const controller = new AbortController();
    abortRef.current = controller;

    const isNyxId = selectedService === 'nyxid-chat';
    const events: RuntimeEvent[] = [];

    const onFrame = (frame: any) => {
      const evt = normalizeBackendSseFrame(frame);
      if (!evt) return;
      events.push(evt);
      setDebugEvents([...events]);

      if (evt.type === 'TEXT_MESSAGE_CONTENT') {
        const delta = String(evt.delta || '');
        setMessages(prev => {
          const updated = [...prev];
          const last = updated[updated.length - 1];
          if (last && last.role === 'assistant') {
            updated[updated.length - 1] = { ...last, content: last.content + delta };
          }
          return updated;
        });
      }
    };

    try {
      if (isNyxId) {
        const actorId = await ensureConversation();
        await api.nyxidChat.streamMessage(scopeId, actorId, text, onFrame, controller.signal);
      } else {
        await api.scope.streamInvoke(scopeId, selectedService, text, onFrame, controller.signal);
      }
      setMessages(prev => {
        const updated = [...prev];
        const last = updated[updated.length - 1];
        if (last && last.role === 'assistant') {
          updated[updated.length - 1] = { ...last, status: 'complete', events };
        }
        return updated;
      });
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        setMessages(prev => {
          const updated = [...prev];
          const last = updated[updated.length - 1];
          if (last && last.role === 'assistant') {
            updated[updated.length - 1] = { ...last, status: 'error', error: e?.message || String(e), events };
          }
          return updated;
        });
      }
    } finally {
      setIsStreaming(false);
      abortRef.current = null;
    }
  }, [scopeId, isStreaming, selectedService, ensureConversation]);

  const handleStop = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const handleNewChat = useCallback(() => {
    setMessages([]);
    setDebugEvents([]);
    setCurrentActorId('');
  }, []);

  if (!scopeId) {
    return (
      <>
        <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
          <div>
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Scope</div>
            <div className="text-[18px] font-bold text-gray-800">Not Logged In</div>
          </div>
        </header>
        <div className="flex-1 flex items-center justify-center text-gray-400 text-[14px]">
          Sign in with NyxID to access your scope.
        </div>
      </>
    );
  }

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <header className="flex-shrink-0 h-[56px] border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-5 flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="text-[14px] font-semibold text-gray-800">Chat</div>
          <ServiceSelector services={services} selected={selectedService} onSelect={setSelectedService} />
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleNewChat}
            className="rounded-lg border border-[#E6E3DE] px-3 py-1.5 text-[12px] text-gray-600 hover:bg-[#F7F5F2]"
          >
            New Chat
          </button>
          <button
            onClick={() => setShowDebug(v => !v)}
            className={`rounded-lg border px-3 py-1.5 text-[12px] ${
              showDebug ? 'border-blue-300 bg-blue-50 text-blue-600' : 'border-[#E6E3DE] text-gray-400 hover:bg-[#F7F5F2]'
            }`}
          >
            Debug
          </button>
        </div>
      </header>

      {/* Chat Area */}
      <div className="flex-1 min-h-0 overflow-auto bg-[#F2F1EE]">
        <div className="max-w-3xl mx-auto py-6 px-4 space-y-4">
          {messages.length === 0 && (
            <div className="flex flex-col items-center justify-center py-20 text-center">
              <div className="w-14 h-14 rounded-full bg-purple-100 flex items-center justify-center mb-4">
                <span className="text-[24px] text-purple-600 font-bold">N</span>
              </div>
              <div className="text-[16px] font-semibold text-gray-700 mb-1">
                {selectedService === 'nyxid-chat' ? 'NyxID Chat' : selectedService}
              </div>
              <div className="text-[13px] text-gray-400 max-w-sm">
                {selectedService === 'nyxid-chat'
                  ? 'Ask me about NyxID services, credentials, or Aevatar configuration.'
                  : 'Send a message to invoke this service.'}
              </div>
            </div>
          )}
          {messages.map(msg => (
            <ChatBubble key={msg.id} msg={msg} />
          ))}
          <div ref={scrollRef} />
        </div>
      </div>

      {/* Debug Panel (collapsible) */}
      {showDebug && debugEvents.length > 0 && (
        <div className="flex-shrink-0 border-t border-[#E6E3DE] bg-[#FAFAF8] px-4 py-2">
          <DebugPanel events={debugEvents} />
        </div>
      )}

      {/* Input */}
      <div className="flex-shrink-0 border-t border-[#E6E3DE] bg-white px-4 py-3">
        <div className="max-w-3xl mx-auto">
          <ChatInput
            onSend={handleSend}
            onStop={handleStop}
            isStreaming={isStreaming}
            disabled={!scopeId}
          />
        </div>
      </div>
    </div>
  );
}
