import { useCallback, useEffect, useRef, useState } from 'react';
import {
  normalizeBackendSseFrame,
  extractStepCompletedOutput,
  extractReasoningDelta,
  isRawObserved,
  type RuntimeEvent,
} from './sseUtils';
import type { ChatMessage, ServiceOption, StepInfo, ToolCallInfo, ConversationMeta } from './chatTypes';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';

// ── Constants ──────────────────────────────────────────────────────────────────

/** Built-in default workflow YAML — must match the backend fallback in ScopeServiceEndpoints. */
const DEFAULT_CHAT_WORKFLOW_YAML = `\
name: default_chat
description: Built-in default single-turn chat.

roles:
  - id: assistant
    name: Assistant
    system_prompt: |
      You are a helpful assistant.

steps:
  - id: answer
    type: llm_call
    role: assistant
    parameters: {}
`;

// ── Helpers ─────────────────────────────────────────────────────────────────────

function genId() {
  return crypto.randomUUID?.() ?? Math.random().toString(36).slice(2);
}

/** Minimal markdown-ish rendering: code blocks, inline code, bold, newlines */
function renderContent(text: string) {
  if (!text) return null;
  const parts = text.split(/(```[\s\S]*?```)/g);
  return parts.map((part, i) => {
    if (part.startsWith('```') && part.endsWith('```')) {
      const inner = part.slice(3, -3);
      const newlineIdx = inner.indexOf('\n');
      const lang = newlineIdx > 0 ? inner.slice(0, newlineIdx).trim() : '';
      const code = newlineIdx > 0 ? inner.slice(newlineIdx + 1) : inner;
      return (
        <div key={i} className="my-2 rounded-lg overflow-hidden border border-gray-200">
          {lang && (
            <div className="px-3 py-1 bg-gray-100 text-[11px] font-mono text-gray-500 border-b border-gray-200">
              {lang}
            </div>
          )}
          <pre className="px-3 py-2 bg-gray-50 text-[13px] font-mono leading-5 overflow-x-auto whitespace-pre">
            {code}
          </pre>
        </div>
      );
    }
    return (
      <span key={i}>
        {part.split('\n').map((line, li, arr) => (
          <span key={li}>
            {renderInline(line)}
            {li < arr.length - 1 && <br />}
          </span>
        ))}
      </span>
    );
  });
}

function renderInline(text: string) {
  const parts = text.split(/(`[^`]+`)/g);
  return parts.map((part, i) => {
    if (part.startsWith('`') && part.endsWith('`')) {
      return (
        <code key={i} className="px-1 py-0.5 rounded bg-gray-100 text-[12px] font-mono text-pink-600">
          {part.slice(1, -1)}
        </code>
      );
    }
    return part.split(/(\*\*[^*]+\*\*)/g).map((seg, j) => {
      if (seg.startsWith('**') && seg.endsWith('**')) {
        return <strong key={`${i}-${j}`}>{seg.slice(2, -2)}</strong>;
      }
      return <span key={`${i}-${j}`}>{seg}</span>;
    });
  });
}

// ── Step Indicator ─────────────────────────────────────────────────────────────

function StepIndicator({ step }: { step: StepInfo }) {
  const isRunning = step.status === 'running';
  return (
    <div className="flex items-center gap-2 py-1">
      <div className={`w-4 h-4 flex items-center justify-center rounded-full ${
        isRunning ? 'bg-amber-100' : 'bg-green-100'
      }`}>
        {isRunning ? (
          <span className="block w-2 h-2 rounded-full bg-amber-400 animate-pulse" />
        ) : (
          <svg className="w-2.5 h-2.5 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
          </svg>
        )}
      </div>
      <span className="text-[12px] text-gray-400 font-medium">{step.name || 'Processing'}</span>
      {step.finishedAt && step.startedAt && (
        <span className="text-[11px] text-gray-300 ml-auto">
          {((step.finishedAt - step.startedAt) / 1000).toFixed(1)}s
        </span>
      )}
    </div>
  );
}

// ── Tool Call Indicator ────────────────────────────────────────────────────────

function ToolCallIndicator({ tool }: { tool: ToolCallInfo }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="py-1">
      <button
        onClick={() => tool.result && setOpen(v => !v)}
        className="flex items-center gap-2 text-[12px] text-gray-400 hover:text-gray-600"
      >
        <span className={`inline-block w-3 h-3 rounded ${tool.status === 'running' ? 'bg-blue-100' : 'bg-gray-100'}`}>
          {tool.status === 'running' ? (
            <span className="block w-1.5 h-1.5 mx-auto mt-[3px] rounded-full bg-blue-400 animate-pulse" />
          ) : (
            <svg className="w-3 h-3 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M11.42 15.17l-5.59-5.59a1.5 1.5 0 010-2.12l.88-.88a1.5 1.5 0 012.12 0l3.59 3.59 7.59-7.59a1.5 1.5 0 012.12 0l.88.88a1.5 1.5 0 010 2.12l-9.59 9.59z" />
            </svg>
          )}
        </span>
        <span className="font-mono">{tool.name || tool.id}</span>
      </button>
      {open && tool.result && (
        <pre className="mt-1 ml-5 text-[11px] font-mono text-gray-400 bg-gray-50 rounded px-2 py-1 max-h-[100px] overflow-auto whitespace-pre-wrap">
          {tool.result.slice(0, 500)}
        </pre>
      )}
    </div>
  );
}

// ── Thinking Block ─────────────────────────────────────────────────────────────

function ThinkingBlock({ text, isStreaming }: { text: string; isStreaming: boolean }) {
  const [open, setOpen] = useState(false);
  if (!text) return null;
  return (
    <div className="mb-2">
      <button
        onClick={() => setOpen(v => !v)}
        className="flex items-center gap-1.5 text-[12px] text-gray-400 hover:text-gray-600 py-1"
      >
        <svg
          className={`w-3 h-3 transition-transform ${open ? 'rotate-90' : ''}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <span>Thinking</span>
        {isStreaming && <span className="inline-block w-1.5 h-1.5 rounded-full bg-purple-400 animate-pulse" />}
      </button>
      {open && (
        <div className="ml-4 pl-3 border-l-2 border-purple-100 text-[13px] text-gray-400 italic whitespace-pre-wrap max-h-[200px] overflow-auto">
          {text}
        </div>
      )}
    </div>
  );
}

// ── Chat Message Bubble ─────────────────────────────────────────────────────────

function ChatBubble({ msg }: { msg: ChatMessage }) {
  const isUser = msg.role === 'user';
  const [stepsOpen, setStepsOpen] = useState(false);
  const hasSteps = msg.steps && msg.steps.length > 0;
  const hasTools = msg.toolCalls && msg.toolCalls.length > 0;

  if (isUser) {
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] rounded-2xl rounded-br-md bg-[#2563eb] text-white px-4 py-3 text-[14px] leading-relaxed">
          <div className="whitespace-pre-wrap break-words">{msg.content}</div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex gap-3">
      <div className="flex-shrink-0 w-7 h-7 rounded-full bg-gradient-to-br from-violet-500 to-indigo-600 flex items-center justify-center mt-1">
        <svg className="w-3.5 h-3.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9.75 3.104v5.714a2.25 2.25 0 01-.659 1.591L5 14.5M9.75 3.104c-.251.023-.501.05-.75.082m.75-.082a24.301 24.301 0 014.5 0m0 0v5.714c0 .597.237 1.17.659 1.591L19.8 15.3M14.25 3.104c.251.023.501.05.75.082M19.8 15.3l-1.57.393A9.065 9.065 0 0112 15a9.065 9.065 0 00-6.23.693L5 14.5m14.8.8l1.402 1.402c1.232 1.232.65 3.318-1.067 3.611A48.309 48.309 0 0112 21c-2.773 0-5.491-.235-8.135-.687-1.718-.293-2.3-2.379-1.067-3.61L5 14.5" />
        </svg>
      </div>

      <div className="flex-1 min-w-0 max-w-[85%]">
        {/* Thinking */}
        {msg.thinking && (
          <ThinkingBlock text={msg.thinking} isStreaming={msg.status === 'streaming'} />
        )}

        {/* Steps + Tool Calls (collapsible) */}
        {(hasSteps || hasTools) && (
          <div className="mb-1">
            <button
              onClick={() => setStepsOpen(v => !v)}
              className="flex items-center gap-1.5 text-[12px] text-gray-400 hover:text-gray-600 py-1"
            >
              <svg
                className={`w-3 h-3 transition-transform ${stepsOpen ? 'rotate-90' : ''}`}
                fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
              >
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
              </svg>
              <span>
                {(msg.steps?.length || 0) + (msg.toolCalls?.length || 0)} action{((msg.steps?.length || 0) + (msg.toolCalls?.length || 0)) > 1 ? 's' : ''}
              </span>
              {(msg.steps?.some(s => s.status === 'running') || msg.toolCalls?.some(t => t.status === 'running')) && (
                <span className="inline-block w-1.5 h-1.5 rounded-full bg-amber-400 animate-pulse" />
              )}
            </button>
            {stepsOpen && (
              <div className="pl-1 border-l-2 border-gray-100 ml-1.5 mb-2">
                {msg.steps?.map((step, i) => <StepIndicator key={`s-${i}`} step={step} />)}
                {msg.toolCalls?.map((tool, i) => <ToolCallIndicator key={`t-${i}`} tool={tool} />)}
              </div>
            )}
          </div>
        )}

        {/* Text content */}
        <div className="text-[14px] leading-relaxed text-gray-800">
          <div className="break-words">
            {renderContent(msg.content)}
            {msg.status === 'streaming' && msg.content && (
              <span className="inline-block w-[2px] h-[18px] bg-gray-400 animate-blink ml-0.5 align-text-bottom" />
            )}
          </div>
          {!msg.content && msg.status === 'streaming' && (
            <div className="flex items-center gap-1.5 py-2">
              <span className="block w-1.5 h-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '0ms' }} />
              <span className="block w-1.5 h-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '200ms' }} />
              <span className="block w-1.5 h-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '400ms' }} />
            </div>
          )}
        </div>

        {/* Error */}
        {msg.status === 'error' && msg.error && (
          <div className="mt-2 flex items-start gap-2 rounded-lg bg-red-50 border border-red-200 px-3 py-2">
            <svg className="w-4 h-4 text-red-400 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z" />
            </svg>
            <span className="text-[13px] text-red-600">{msg.error}</span>
          </div>
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
    <div className="relative">
      <div className="flex items-end rounded-2xl border border-[#E6E3DE] bg-white shadow-sm focus-within:ring-2 focus-within:ring-blue-400 focus-within:border-transparent">
        <textarea
          ref={textareaRef}
          rows={1}
          className="flex-1 resize-none bg-transparent px-4 py-3 text-[14px] focus:outline-none placeholder:text-gray-400"
          value={text}
          onChange={e => { setText(e.target.value); handleInput(); }}
          onKeyDown={handleKeyDown}
          placeholder="Send a message..."
          disabled={disabled}
        />
        <div className="flex-shrink-0 p-1.5">
          {isStreaming ? (
            <button
              onClick={onStop}
              className="w-8 h-8 flex items-center justify-center rounded-lg bg-red-500 hover:bg-red-600 text-white transition-colors"
              title="Stop"
            >
              <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
                <rect x="6" y="6" width="12" height="12" rx="1" />
              </svg>
            </button>
          ) : (
            <button
              onClick={handleSend}
              disabled={!text.trim() || disabled}
              className="w-8 h-8 flex items-center justify-center rounded-lg bg-[#18181B] text-white hover:bg-[#333] disabled:opacity-20 disabled:cursor-not-allowed transition-colors"
              title="Send"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 10.5L12 3m0 0l7.5 7.5M12 3v18" />
              </svg>
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Debug Panel ────────────────────────────────────────────────────────────────

function DebugPanel({ events }: { events: RuntimeEvent[] }) {
  if (events.length === 0) return null;
  return (
    <div className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden max-h-[240px] overflow-auto">
      <div className="px-4 py-2 border-b border-[#E6E3DE] text-[11px] font-semibold uppercase tracking-wider text-gray-400 sticky top-0 bg-white">
        Raw Events ({events.length})
      </div>
      <div className="divide-y divide-[#F0EDE8]">
        {events.map((evt, i) => (
          <div key={i} className="px-4 py-1.5 text-[11px] font-mono text-gray-600 flex gap-2">
            <span className="text-gray-300 w-4 text-right flex-shrink-0">{i + 1}</span>
            <span className={`font-semibold flex-shrink-0 ${
              evt.type === 'RUN_ERROR' ? 'text-red-500' :
              evt.type === 'TEXT_MESSAGE_CONTENT' ? 'text-blue-500' :
              evt.type.startsWith('STEP_') ? 'text-amber-500' :
              evt.type.startsWith('RUN_') ? 'text-green-500' :
              evt.type.startsWith('TOOL_') ? 'text-purple-500' :
              'text-gray-500'
            }`}>{evt.type}</span>
            {evt.type === 'TEXT_MESSAGE_CONTENT' && (
              <span className="text-gray-400 truncate">{String(evt.delta || '').slice(0, 80)}</span>
            )}
            {evt.type === 'STEP_STARTED' && (
              <span className="text-gray-400">{String(evt.stepName || '')}</span>
            )}
            {evt.type === 'CUSTOM' && (
              <span className="text-gray-400 truncate">{String(evt.name || '')}</span>
            )}
            {evt.type === 'RUN_ERROR' && (
              <span className="text-red-400 truncate">{String(evt.message || '')}</span>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Conversation Sidebar ───────────────────────────────────────────────────────

function ConversationSidebar({
  conversations,
  activeId,
  onSelect,
  onDelete,
  onNewChat,
  open,
  onToggle,
}: {
  conversations: ConversationMeta[];
  activeId: string | null;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
  onNewChat: () => void;
  open: boolean;
  onToggle: () => void;
}) {
  if (!open) {
    return (
      <div className="flex-shrink-0 border-r border-[#E6E3DE] bg-white flex flex-col items-center py-3 w-[40px]">
        <button onClick={onToggle} className="p-1.5 rounded-lg hover:bg-[#F7F5F2] text-gray-400" title="Show conversations">
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M8.25 4.5l7.5 7.5-7.5 7.5" />
          </svg>
        </button>
      </div>
    );
  }

  return (
    <aside className="w-[260px] flex-shrink-0 border-r border-[#E6E3DE] bg-white flex flex-col">
      {/* Header */}
      <div className="px-3 py-2.5 border-b border-[#E6E3DE] flex items-center justify-between">
        <span className="text-[12px] font-semibold text-gray-500 uppercase tracking-wider">History</span>
        <div className="flex items-center gap-1">
          <button
            onClick={onNewChat}
            className="p-1 rounded-md hover:bg-[#F7F5F2] text-gray-400 hover:text-gray-600"
            title="New chat"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
            </svg>
          </button>
          <button onClick={onToggle} className="p-1 rounded-md hover:bg-[#F7F5F2] text-gray-400 hover:text-gray-600" title="Hide sidebar">
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 19.5L8.25 12l7.5-7.5" />
            </svg>
          </button>
        </div>
      </div>

      {/* Conversation list */}
      <div className="flex-1 min-h-0 overflow-auto">
        {conversations.length === 0 && (
          <div className="px-4 py-6 text-center text-[12px] text-gray-300">No conversations yet</div>
        )}
        {conversations.map(conv => {
          const isActive = conv.id === activeId;
          return (
            <div
              key={conv.id}
              className={`group relative w-full text-left px-3 py-2.5 border-b border-[#F0EDE8] transition-colors cursor-pointer ${
                isActive ? 'bg-blue-50 border-l-2 border-l-blue-500' : 'hover:bg-[#F7F5F2] border-l-2 border-l-transparent'
              }`}
              onClick={() => onSelect(conv.id)}
            >
              <div className="text-[13px] font-medium text-gray-700 truncate pr-6">{conv.title || 'Untitled'}</div>
              <div className="text-[11px] text-gray-400 mt-0.5">
                {conv.messageCount} msg{conv.messageCount !== 1 ? 's' : ''}
                {' · '}
                {formatRelativeTime(conv.updatedAt)}
              </div>
              <button
                onClick={e => { e.stopPropagation(); onDelete(conv.id); }}
                className="absolute top-2.5 right-2 p-1 rounded-md opacity-0 group-hover:opacity-100 hover:bg-red-50 text-gray-300 hover:text-red-500 transition-all"
                title="Delete conversation"
              >
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0" />
                </svg>
              </button>
            </div>
          );
        })}
      </div>
    </aside>
  );
}

function formatRelativeTime(isoString: string) {
  if (!isoString) return '';
  const diff = Date.now() - new Date(isoString).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d ago`;
  return new Date(isoString).toLocaleDateString();
}

// ── Main ScopePage ──────────────────────────────────────────────────────────────

export default function ScopePage() {
  const scopeId = nyxid.loadSession()?.user.sub || '';

  // Services
  const [services, setServices] = useState<ServiceOption[]>([
    { id: '__default-chat__', label: 'Default Chat', kind: 'draft-run' },
    { id: 'nyxid-chat', label: 'NyxID Chat', kind: 'nyxid-chat' },
  ]);
  const [selectedService, setSelectedService] = useState('__default-chat__');

  // Draft Run YAML (optional)
  const [draftYaml, setDraftYaml] = useState(DEFAULT_CHAT_WORKFLOW_YAML);
  const [showYamlInput, setShowYamlInput] = useState(false);

  // NyxID Chat: track whether we've ensured the scope binding this session
  const nyxidChatBoundRef = useRef(false);

  // Chat
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [debugEvents, setDebugEvents] = useState<RuntimeEvent[]>([]);
  const [showDebug, setShowDebug] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Chat history persistence
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [activeConvId, setActiveConvId] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Load services on mount
  useEffect(() => {
    if (!scopeId) return;
    api.scope.listServices(scopeId).then(svcList => {
      const base: ServiceOption[] = [
        { id: '__default-chat__', label: 'Default Chat', kind: 'draft-run' },
        { id: 'nyxid-chat', label: 'NyxID Chat', kind: 'nyxid-chat' },
      ];
      if (Array.isArray(svcList)) {
        // Built-in service IDs that already have hardcoded entries above.
        const builtinIds = new Set(base.map(b => b.id));
        for (const s of svcList) {
          const sid = s?.serviceId || s?.ServiceId;
          const name = s?.displayName || s?.DisplayName || sid;
          // Skip services whose displayName matches a built-in entry (e.g. "NyxID Chat")
          // to avoid duplicates when the backend returns a previously bound service.
          const isDuplicate = builtinIds.has(sid)
            || base.some(b => b.label === name);
          if (sid && !isDuplicate) base.push({ id: sid, label: name, kind: 'service' });
        }
      }
      setServices(base);
    }).catch(() => {});
  }, [scopeId]);

  // Auto-scroll on new messages
  useEffect(() => {
    scrollRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const activeService = services.find(s => s.id === selectedService) || services[0];

  // Reset NyxID Chat binding flag when scope changes
  useEffect(() => {
    nyxidChatBoundRef.current = false;
  }, [scopeId]);

  // Load conversation index when scope changes
  useEffect(() => {
    if (!scopeId) return;
    api.chatHistory.getIndex(scopeId).then(data => {
      setConversations(data?.conversations ?? []);
    }).catch(() => {});
  }, [scopeId]);

  // Save current conversation to chrono-storage (called after streaming completes)
  const saveCurrentConversation = useCallback((msgs: ChatMessage[]) => {
    if (!scopeId || msgs.length === 0) return;
    const convId = activeConvId || `conv-${genId()}`;
    const firstUserMsg = msgs.find(m => m.role === 'user');
    const title = (firstUserMsg?.content || 'Untitled').slice(0, 60);
    const now = new Date().toISOString();
    const storedMsgs = msgs
      .filter(m => m.status !== 'streaming')
      .map(m => ({
        id: m.id, role: m.role, content: m.content, timestamp: m.timestamp,
        status: m.status === 'streaming' ? 'complete' : m.status,
        ...(m.error ? { error: m.error } : {}),
        ...(m.thinking ? { thinking: m.thinking } : {}),
      }));
    const meta: ConversationMeta = {
      id: convId,
      title,
      serviceId: activeService.id,
      serviceKind: activeService.kind,
      createdAt: conversations.find(c => c.id === convId)?.createdAt || now,
      updatedAt: now,
      messageCount: storedMsgs.length,
    };
    // Update local state immediately
    setActiveConvId(convId);
    setConversations(prev => {
      const filtered = prev.filter(c => c.id !== convId);
      return [meta, ...filtered];
    });
    // Persist (fire-and-forget with debounce)
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      api.chatHistory.saveConversation(scopeId, convId, meta, storedMsgs).catch(() => {});
    }, 500);
  }, [scopeId, activeConvId, activeService, conversations]);

  // ── Send message ──
  const handleSend = useCallback(async (text: string) => {
    if (!scopeId || isStreaming) return;

    const userMsg: ChatMessage = {
      id: genId(), role: 'user', content: text, timestamp: Date.now(), status: 'complete',
    };
    const assistantMsg: ChatMessage = {
      id: genId(), role: 'assistant', content: '', timestamp: Date.now(), status: 'streaming',
      steps: [], toolCalls: [], thinking: '',
    };

    setMessages(prev => [...prev, userMsg, assistantMsg]);
    setIsStreaming(true);
    setDebugEvents([]);

    const controller = new AbortController();
    abortRef.current = controller;

    const events: RuntimeEvent[] = [];
    const steps: StepInfo[] = [];
    const toolCalls: ToolCallInfo[] = [];
    let thinking = '';
    let contentText = '';

    const updateAssistant = (patch: Partial<ChatMessage>) => {
      setMessages(prev => {
        const updated = [...prev];
        const last = updated[updated.length - 1];
        if (last?.role === 'assistant') {
          updated[updated.length - 1] = { ...last, ...patch };
        }
        return updated;
      });
    };

    const onFrame = (frame: any) => {
      const evt = normalizeBackendSseFrame(frame);
      if (!evt) return;
      events.push(evt);
      setDebugEvents([...events]);

      switch (evt.type) {
        case 'TEXT_MESSAGE_CONTENT': {
          const delta = String(evt.delta || '');
          contentText += delta;
          updateAssistant({ content: contentText });
          break;
        }

        case 'STEP_STARTED': {
          const stepName = String(evt.stepName || '');
          steps.push({ name: stepName, status: 'running', startedAt: Date.now() });
          updateAssistant({ steps: [...steps] });
          break;
        }

        case 'STEP_FINISHED': {
          const stepName = String(evt.stepName || '');
          const existing = steps.find(s => s.name === stepName && s.status === 'running');
          if (existing) {
            existing.status = 'done';
            existing.finishedAt = Date.now();
          }
          updateAssistant({ steps: [...steps] });
          break;
        }

        case 'TOOL_CALL_START': {
          const toolName = String(evt.toolName || '');
          const toolCallId = String(evt.toolCallId || '');
          toolCalls.push({ id: toolCallId, name: toolName, status: 'running' });
          updateAssistant({ toolCalls: [...toolCalls] });
          break;
        }

        case 'TOOL_CALL_END': {
          const toolCallId = String(evt.toolCallId || '');
          const existing = toolCalls.find(t => t.id === toolCallId && t.status === 'running');
          if (existing) {
            existing.status = 'done';
            existing.result = String(evt.result || '');
          }
          updateAssistant({ toolCalls: [...toolCalls] });
          break;
        }

        case 'RUN_ERROR': {
          updateAssistant({ status: 'error', error: String(evt.message || 'Unknown error') });
          break;
        }

        case 'CUSTOM': {
          // Extract text from step completed output
          const stepOutput = extractStepCompletedOutput(evt);
          if (stepOutput && !contentText) {
            // Use step output as the response text if no streaming text was received
            contentText = stepOutput;
            updateAssistant({ content: contentText });
            break;
          }

          // Extract reasoning/thinking delta
          const reasoningDelta = extractReasoningDelta(evt);
          if (reasoningDelta) {
            thinking += reasoningDelta;
            updateAssistant({ thinking });
            break;
          }

          // Skip raw observed events (catch-all, not user-facing)
          if (isRawObserved(evt)) break;

          break;
        }
      }
    };

    try {
      if (activeService.kind === 'nyxid-chat') {
        // Bind NyxIdChatGAgent as default scope service on first use (idempotent PUT).
        // preferredActorId ensures the same actor is reused for conversation history.
        if (!nyxidChatBoundRef.current) {
          await api.scope.bindGAgent(
            scopeId,
            'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent',
            `NyxIdChat:${scopeId}`,
            'NyxID Chat',
          );
          nyxidChatBoundRef.current = true;
        }
        await api.scope.streamDefaultChat(scopeId, text, undefined, onFrame, controller.signal);
      } else if (activeService.kind === 'draft-run') {
        const yamls = draftYaml.trim() ? [draftYaml.trim()] : undefined;
        if (yamls) {
          // Draft run with explicit YAML
          await api.scope.streamDraftRun(scopeId, text, yamls, onFrame, controller.signal);
        } else {
          // No YAML — use scope-level default chat stream
          await api.scope.streamDefaultChat(scopeId, text, undefined, onFrame, controller.signal);
        }
      } else {
        await api.scope.streamInvoke(scopeId, activeService.id, text, onFrame, controller.signal);
      }
      // Only mark complete if no error was already set by RUN_ERROR during streaming.
      setMessages(prev => {
        const updated = [...prev];
        const last = updated[updated.length - 1];
        if (last?.role === 'assistant' && last.status !== 'error') {
          updated[updated.length - 1] = { ...last, status: 'complete', events, steps: [...steps], toolCalls: [...toolCalls], thinking };
        }
        saveCurrentConversation(updated);
        return updated;
      });
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        const errorText = e?.message || e?.code || JSON.stringify(e);
        updateAssistant({
          status: 'error', error: errorText, events, steps: [...steps], toolCalls: [...toolCalls], thinking,
        });
        setMessages(prev => { saveCurrentConversation(prev); return prev; });
      }
    } finally {
      setIsStreaming(false);
      abortRef.current = null;
    }
  }, [scopeId, isStreaming, activeService, draftYaml, saveCurrentConversation]);

  const handleStop = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const handleNewChat = useCallback(() => {
    setMessages([]);
    setDebugEvents([]);
    setActiveConvId(null);
    nyxidChatBoundRef.current = false;
  }, []);

  const handleSelectConversation = useCallback(async (convId: string) => {
    if (!scopeId || convId === activeConvId) return;
    try {
      const msgs = await api.chatHistory.getConversation(scopeId, convId);
      setMessages(msgs.map(m => ({
        id: m.id, role: m.role as 'user' | 'assistant', content: m.content,
        timestamp: m.timestamp, status: (m.status || 'complete') as ChatMessage['status'],
        error: m.error ?? undefined, thinking: m.thinking ?? undefined,
      })));
      setActiveConvId(convId);
      setDebugEvents([]);
    } catch { /* ignore */ }
  }, [scopeId, activeConvId]);

  const handleDeleteConversation = useCallback(async (convId: string) => {
    if (!scopeId) return;
    try {
      await api.chatHistory.deleteConversation(scopeId, convId);
      setConversations(prev => prev.filter(c => c.id !== convId));
      if (activeConvId === convId) {
        setMessages([]);
        setActiveConvId(null);
        setDebugEvents([]);
      }
    } catch { /* ignore */ }
  }, [scopeId, activeConvId]);

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
      <header className="flex-shrink-0 h-[52px] border-b border-[#E6E3DE] bg-white/95 backdrop-blur-sm px-5 flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="text-[14px] font-semibold text-gray-800">Chat</div>
          <ServiceSelector services={services} selected={selectedService} onSelect={setSelectedService} />
          {activeService.kind === 'draft-run' && (
            <button
              onClick={() => setShowYamlInput(v => !v)}
              className={`rounded-lg border px-2.5 py-1 text-[11px] font-medium transition-colors ${
                showYamlInput ? 'border-indigo-300 bg-indigo-50 text-indigo-600' : 'border-[#E6E3DE] text-gray-400 hover:text-gray-600 hover:bg-gray-50'
              }`}
            >
              YAML
            </button>
          )}
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleNewChat}
            className="rounded-lg border border-[#E6E3DE] px-3 py-1.5 text-[12px] text-gray-500 hover:bg-[#F7F5F2] hover:text-gray-700 transition-colors"
          >
            New Chat
          </button>
          <button
            onClick={() => setShowDebug(v => !v)}
            className={`rounded-lg border px-2.5 py-1.5 text-[11px] font-medium transition-colors ${
              showDebug ? 'border-blue-300 bg-blue-50 text-blue-600' : 'border-[#E6E3DE] text-gray-400 hover:bg-[#F7F5F2]'
            }`}
          >
            Debug
          </button>
        </div>
      </header>

      {/* Body: Sidebar + Chat */}
      <div className="flex-1 min-h-0 flex">
        {/* Conversation Sidebar */}
        <ConversationSidebar
          conversations={conversations}
          activeId={activeConvId}
          onSelect={handleSelectConversation}
          onDelete={handleDeleteConversation}
          onNewChat={handleNewChat}
          open={sidebarOpen}
          onToggle={() => setSidebarOpen(v => !v)}
        />

        {/* Chat Column */}
        <div className="flex-1 min-h-0 flex flex-col">

      {/* Workflow YAML editor for draft-run */}
      {activeService.kind === 'draft-run' && showYamlInput && (
        <div className="flex-shrink-0 border-b border-[#E6E3DE] bg-[#FAFAF8] px-4 py-3">
          <div className="max-w-3xl mx-auto">
            <div className="flex items-center justify-between mb-1.5">
              <label className="text-[11px] font-semibold text-gray-400 uppercase tracking-wider">
                Workflow YAML
              </label>
              {draftYaml !== DEFAULT_CHAT_WORKFLOW_YAML && (
                <button
                  onClick={() => setDraftYaml(DEFAULT_CHAT_WORKFLOW_YAML)}
                  className="text-[10px] text-indigo-500 hover:text-indigo-700 font-medium"
                >
                  Reset to default
                </button>
              )}
            </div>
            <textarea
              rows={12}
              className="w-full rounded-lg border border-[#E6E3DE] bg-white px-4 py-3 text-[12px] leading-[1.6] font-mono text-gray-700 focus:outline-none focus:ring-2 focus:ring-blue-400 resize-y"
              value={draftYaml}
              onChange={e => setDraftYaml(e.target.value)}
              spellCheck={false}
            />
          </div>
        </div>
      )}

      {/* Chat Area */}
      <div className="flex-1 min-h-0 overflow-auto bg-[#FAFAF8]">
        <div className="max-w-3xl mx-auto py-6 px-4 space-y-5">
          {messages.length === 0 && (
            <div className="flex flex-col items-center justify-center py-20 text-center">
              <div className="w-12 h-12 rounded-2xl bg-gradient-to-br from-violet-500 to-indigo-600 flex items-center justify-center mb-4 shadow-lg shadow-indigo-200">
                <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9.75 3.104v5.714a2.25 2.25 0 01-.659 1.591L5 14.5M9.75 3.104c-.251.023-.501.05-.75.082m.75-.082a24.301 24.301 0 014.5 0m0 0v5.714c0 .597.237 1.17.659 1.591L19.8 15.3M14.25 3.104c.251.023.501.05.75.082M19.8 15.3l-1.57.393A9.065 9.065 0 0112 15a9.065 9.065 0 00-6.23.693L5 14.5m14.8.8l1.402 1.402c1.232 1.232.65 3.318-1.067 3.611A48.309 48.309 0 0112 21c-2.773 0-5.491-.235-8.135-.687-1.718-.293-2.3-2.379-1.067-3.61L5 14.5" />
                </svg>
              </div>
              <div className="text-[16px] font-semibold text-gray-700 mb-1">
                {activeService.label}
              </div>
              <div className="text-[13px] text-gray-400 max-w-sm">
                {activeService.kind === 'draft-run'
                  ? 'Chat with the scope\'s default service. Toggle YAML to use a custom workflow.'
                  : activeService.kind === 'nyxid-chat'
                  ? 'Chat with NyxID about services, credentials, and configuration.'
                  : `Invoke the "${activeService.label}" service with a chat message.`}
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
        <div className="flex-shrink-0 border-t border-[#E6E3DE] bg-[#FAFAF8] px-4 py-2 max-h-[280px]">
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
          <div className="mt-1.5 text-center text-[11px] text-gray-300">
            {activeService.kind === 'draft-run'
              ? (draftYaml.trim() ? 'Draft Run (custom YAML)' : 'Service: default')
              : activeService.kind === 'service' ? `Service: ${activeService.id}` : 'NyxID Chat'}
            {' · Scope: '}{scopeId.slice(0, 16)}...
          </div>
        </div>
      </div>

        </div>{/* end Chat Column */}
      </div>{/* end Body flex */}
    </div>
  );
}
