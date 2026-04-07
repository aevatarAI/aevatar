import { useCallback, useEffect, useRef, useState } from 'react';
import {
  normalizeBackendSseFrame,
  extractStepCompletedOutput,
  extractReasoningDelta,
  isRawObserved,
  type RuntimeEvent,
} from './sseUtils';
import { parseMarkdownBlocks, sanitizeAssistantMessageContent, tokenizeInlineContent } from './chatContent';
import type { ChatMessage, ServiceOption, StepInfo, ToolCallInfo, ConversationMeta } from './chatTypes';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';

// ── Constants ──────────────────────────────────────────────────────────────────

const NYXID_CHAT_SERVICE_ID = 'nyxid-chat';
const STREAMING_PROXY_SERVICE_ID = 'streaming-proxy';
const STREAMING_PROXY_FIRST_REPLY_BASE_DELAY_MS = 1500;
const STREAMING_PROXY_BETWEEN_REPLY_BASE_DELAY_MS = 2200;
const STREAMING_PROXY_MAX_REPLY_DELAY_MS = 4200;

// ── Helpers ─────────────────────────────────────────────────────────────────────

function genId() {
  return crypto.randomUUID?.() ?? Math.random().toString(36).slice(2);
}

function sleep(ms: number) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function buildStreamingProxyConversationId(roomId: string) {
  return `${STREAMING_PROXY_SERVICE_ID}:${roomId}`;
}

function tryParseStreamingProxyRoomId(conversationId?: string | null) {
  if (!conversationId) return null;
  const prefix = `${STREAMING_PROXY_SERVICE_ID}:`;
  return conversationId.startsWith(prefix) ? conversationId.slice(prefix.length) : null;
}

function buildStreamingProxyProgressMessage(
  joinedParticipants: Iterable<string>,
  phase: 'starting' | 'topic-started' | 'participants-joined',
) {
  const participants = Array.from(joinedParticipants).filter(Boolean);
  if (participants.length > 0) {
    return `正在让这些 participants 参与讨论：${participants.join('、')}。回复生成中...`;
  }

  if (phase === 'topic-started') {
    return '话题已经发到 room 里了，正在连接 Nyx participants...';
  }

  return '正在初始化 Streaming Proxy 讨论...';
}

function buildStreamingProxyTurnMessage(participantName: string, turnIndex: number) {
  const name = participantName.trim() || '下一位 participant';
  if (turnIndex <= 0) {
    return `${name} 正在整理开场观点...`;
  }

  return `${name} 正在斟酌上一轮观点，准备继续回应...`;
}

function buildStreamingProxyWaitingMessage() {
  return '房间里短暂停顿了一下，下一位 participant 正在组织回应...';
}

function getStreamingProxyRevealDelay(content: string, turnIndex: number) {
  const trimmed = content.trim();
  const baseDelay = turnIndex <= 0
    ? STREAMING_PROXY_FIRST_REPLY_BASE_DELAY_MS
    : STREAMING_PROXY_BETWEEN_REPLY_BASE_DELAY_MS;
  const lengthDelay = Math.min(1400, trimmed.length * 4);
  const punctuationMatches = trimmed.match(/[，。！？；：,.!?;:]/g);
  const punctuationDelay = Math.min(500, (punctuationMatches?.length ?? 0) * 70);
  return Math.min(STREAMING_PROXY_MAX_REPLY_DELAY_MS, baseDelay + lengthDelay + punctuationDelay);
}

function isStreamingProxyServiceCandidate(
  serviceId: string,
  label: string,
  endpoints: Array<{ endpointId: string; displayName: string; kind: string }>,
) {
  if (serviceId === STREAMING_PROXY_SERVICE_ID) return true;

  const normalizedLabel = label.trim().toLowerCase();
  if (normalizedLabel.includes('streamingproxy') || normalizedLabel.includes('streaming proxy')) {
    return true;
  }

  const endpointIds = new Set(endpoints.map(endpoint => endpoint.endpointId.trim().toLowerCase()));
  return endpointIds.has('initializeroom')
    && endpointIds.has('postmessage')
    && endpointIds.has('joinroom');
}

/** Lightweight markdown rendering for chat content */
type RenderTone = 'assistant' | 'user';

function renderContent(text: string, tone: RenderTone = 'assistant') {
  if (!text) return null;
  return parseMarkdownBlocks(text).map((block, i) => renderBlock(block, tone, i));
}

function renderInline(text: string, tone: RenderTone) {
  return tokenizeInlineContent(text).map((token, i) => {
    if (token.kind === 'code') {
      const codeClass = tone === 'user'
        ? 'px-1 py-0.5 rounded bg-white/15 text-[12px] font-mono text-white'
        : 'px-1 py-0.5 rounded bg-gray-100 text-[12px] font-mono text-pink-600';
      return (
        <code key={i} className={codeClass}>
          {token.text}
        </code>
      );
    }

    if (token.kind === 'link') {
      const linkClass = tone === 'user'
        ? 'underline underline-offset-2 decoration-white/60 hover:decoration-white text-white break-all'
        : 'underline underline-offset-2 decoration-blue-300 hover:decoration-blue-500 text-blue-600 hover:text-blue-700 break-all';
      const content = token.bold ? <strong>{token.text}</strong> : token.text;
      return (
        <a
          key={i}
          href={token.href}
          target="_blank"
          rel="noopener noreferrer"
          className={linkClass}
        >
          {content}
        </a>
      );
    }

    if (token.bold) {
      return <strong key={i}>{token.text}</strong>;
    }

    return <span key={i}>{token.text}</span>;
  });
}

function renderBlock(
  block: ReturnType<typeof parseMarkdownBlocks>[number],
  tone: RenderTone,
  key: number,
) {
  switch (block.kind) {
    case 'code': {
      const containerClass = tone === 'user'
        ? 'my-2 rounded-lg overflow-hidden border border-white/20'
        : 'my-2 rounded-lg overflow-hidden border border-gray-200';
      const headerClass = tone === 'user'
        ? 'px-3 py-1 bg-white/10 text-[11px] font-mono text-white/75 border-b border-white/15'
        : 'px-3 py-1 bg-gray-100 text-[11px] font-mono text-gray-500 border-b border-gray-200';
      const preClass = tone === 'user'
        ? 'px-3 py-2 bg-white/5 text-[13px] font-mono leading-5 overflow-x-auto whitespace-pre text-white'
        : 'px-3 py-2 bg-gray-50 text-[13px] font-mono leading-5 overflow-x-auto whitespace-pre';
      return (
        <div key={key} className={containerClass}>
          {block.lang && (
            <div className={headerClass}>
              {block.lang}
            </div>
          )}
          <pre className={preClass}>
            {block.code}
          </pre>
        </div>
      );
    }

    case 'heading': {
      const sizes = ['text-[22px]', 'text-[19px]', 'text-[17px]', 'text-[15px]', 'text-[14px]', 'text-[13px]'];
      const headingClass = tone === 'user'
        ? `${sizes[Math.min(block.level - 1, sizes.length - 1)]} font-semibold text-white mt-3 mb-1`
        : `${sizes[Math.min(block.level - 1, sizes.length - 1)]} font-semibold text-gray-900 mt-3 mb-1`;
      return (
        <div key={key} className={headingClass}>
          {renderInline(block.text, tone)}
        </div>
      );
    }

    case 'blockquote': {
      const quoteClass = tone === 'user'
        ? 'my-2 border-l-2 border-white/35 pl-3 text-white/90'
        : 'my-2 border-l-2 border-gray-300 pl-3 text-gray-600';
      return (
        <blockquote key={key} className={quoteClass}>
          {renderLines(block.lines, tone)}
        </blockquote>
      );
    }

    case 'unordered-list': {
      const listClass = tone === 'user'
        ? 'my-2 list-disc pl-5 space-y-1 text-white'
        : 'my-2 list-disc pl-5 space-y-1 text-gray-800';
      return (
        <ul key={key} className={listClass}>
          {block.items.map((item, idx) => (
            <li key={idx} className="break-words">
              {renderInline(item, tone)}
            </li>
          ))}
        </ul>
      );
    }

    case 'ordered-list': {
      const listClass = tone === 'user'
        ? 'my-2 list-decimal pl-5 space-y-1 text-white'
        : 'my-2 list-decimal pl-5 space-y-1 text-gray-800';
      return (
        <ol key={key} className={listClass}>
          {block.items.map((item, idx) => (
            <li key={idx} className="break-words">
              {renderInline(item, tone)}
            </li>
          ))}
        </ol>
      );
    }

    case 'thematic-break': {
      const hrClass = tone === 'user'
        ? 'my-3 border-white/20'
        : 'my-3 border-gray-200';
      return <hr key={key} className={hrClass} />;
    }

    case 'paragraph':
    default:
      return (
        <p key={key} className="my-1">
          {renderLines(block.lines, tone)}
        </p>
      );
  }
}

function renderLines(lines: string[], tone: RenderTone) {
  return lines.map((line, lineIndex) => (
    <span key={lineIndex}>
      {renderInline(line, tone)}
      {lineIndex < lines.length - 1 && <br />}
    </span>
  ));
}

function hashAssistantIdentity(value: string) {
  let hash = 0;
  for (const ch of value) {
    hash = ((hash << 5) - hash) + ch.charCodeAt(0);
    hash |= 0;
  }
  return Math.abs(hash);
}

function getAssistantBadgeClasses(seed: string) {
  const variants = [
    'bg-gradient-to-br from-sky-500 to-cyan-500 text-white',
    'bg-gradient-to-br from-emerald-500 to-teal-500 text-white',
    'bg-gradient-to-br from-amber-500 to-orange-500 text-white',
    'bg-gradient-to-br from-rose-500 to-pink-500 text-white',
    'bg-gradient-to-br from-indigo-500 to-violet-500 text-white',
    'bg-gradient-to-br from-fuchsia-500 to-purple-500 text-white',
  ];
  return variants[hashAssistantIdentity(seed) % variants.length];
}

function getAssistantBadgeLabel(name: string) {
  const trimmed = name.trim();
  if (!trimmed) {
    return 'AI';
  }

  const words = trimmed.split(/\s+/).filter(Boolean);
  if (words.length >= 2) {
    return words
      .slice(0, 2)
      .map(word => Array.from(word)[0] ?? '')
      .join('')
      .toUpperCase();
  }

  return Array.from(trimmed).slice(0, 2).join('').toUpperCase();
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
  const displayContent = isUser ? msg.content : sanitizeAssistantMessageContent(msg.content);
  const authorName = !isUser ? msg.authorName?.trim() : '';
  const isParticipantMessage = !isUser && !!authorName && !/^streaming proxy$/i.test(authorName);
  const assistantIdentity = msg.authorId?.trim() || authorName || 'assistant';
  const assistantBadgeClass = isParticipantMessage
    ? getAssistantBadgeClasses(assistantIdentity)
    : 'bg-gradient-to-br from-violet-500 to-indigo-600 text-white';
  const assistantBadgeLabel = isParticipantMessage
    ? getAssistantBadgeLabel(authorName)
    : '';
  const assistantCardClass = isParticipantMessage
    ? 'rounded-2xl border border-slate-200 bg-white px-4 py-3 shadow-sm shadow-slate-200/70'
    : 'rounded-2xl border border-[#E6E3DE] bg-white/90 px-4 py-3 shadow-sm shadow-stone-200/60';

  if (isUser) {
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] rounded-2xl rounded-br-md bg-[#2563eb] text-white px-4 py-3 text-[14px] leading-relaxed">
          <div className="break-words">{renderContent(msg.content, 'user')}</div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex gap-3">
      <div className={`flex-shrink-0 mt-1 flex h-8 w-8 items-center justify-center rounded-full ${assistantBadgeClass}`}>
        {isParticipantMessage ? (
          <span className="text-[10px] font-semibold tracking-wide">{assistantBadgeLabel}</span>
        ) : (
          <svg className="w-3.5 h-3.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9.75 3.104v5.714a2.25 2.25 0 01-.659 1.591L5 14.5M9.75 3.104c-.251.023-.501.05-.75.082m.75-.082a24.301 24.301 0 014.5 0m0 0v5.714c0 .597.237 1.17.659 1.591L19.8 15.3M14.25 3.104c.251.023.501.05.75.082M19.8 15.3l-1.57.393A9.065 9.065 0 0112 15a9.065 9.065 0 00-6.23.693L5 14.5m14.8.8l1.402 1.402c1.232 1.232.65 3.318-1.067 3.611A48.309 48.309 0 0112 21c-2.773 0-5.491-.235-8.135-.687-1.718-.293-2.3-2.379-1.067-3.61L5 14.5" />
          </svg>
        )}
      </div>

      <div className={`flex-1 min-w-0 max-w-[85%] ${assistantCardClass}`}>
        {authorName && (
          <div className="mb-3 flex items-center gap-2 text-[13px] font-semibold text-gray-900">
            <span>{authorName}</span>
            {isParticipantMessage && (
              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] font-medium text-slate-500">
                Participant
              </span>
            )}
          </div>
        )}

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
            {renderContent(displayContent, 'assistant')}
            {msg.status === 'streaming' && displayContent && (
              <span className="inline-block w-[2px] h-[18px] bg-gray-400 animate-blink ml-0.5 align-text-bottom" />
            )}
          </div>
          {!displayContent && msg.status === 'streaming' && (
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
  onInterruptSend,
  onStop,
  isStreaming,
  disabled,
}: {
  onSend: (text: string) => void;
  onInterruptSend: (text: string) => void;
  onStop: () => void;
  isStreaming: boolean;
  disabled: boolean;
}) {
  const [text, setText] = useState('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const hasText = text.trim().length > 0;

  const handleSend = useCallback(() => {
    const trimmed = text.trim();
    if (!trimmed || disabled) return;
    if (isStreaming) {
      onInterruptSend(trimmed);
    } else {
      onSend(trimmed);
    }
    setText('');
    if (textareaRef.current) textareaRef.current.style.height = 'auto';
  }, [text, isStreaming, disabled, onSend, onInterruptSend]);

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
          {isStreaming && !hasText ? (
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
              disabled={!hasText || disabled}
              className={`w-8 h-8 flex items-center justify-center rounded-lg text-white transition-colors disabled:opacity-20 disabled:cursor-not-allowed ${
                isStreaming
                  ? 'bg-amber-500 hover:bg-amber-600'
                  : 'bg-[#18181B] hover:bg-[#333]'
              }`}
              title={isStreaming ? 'Stop current debate and send' : 'Send'}
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
                {conv.serviceId && <span className="font-mono text-gray-500">{conv.serviceId}</span>}
                {conv.serviceId ? ' · ' : ''}
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
    { id: NYXID_CHAT_SERVICE_ID, label: 'NyxID Chat', kind: 'nyxid-chat', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
    { id: STREAMING_PROXY_SERVICE_ID, label: 'Streaming Proxy', kind: 'streaming-proxy', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
  ]);
  const [selectedService, setSelectedService] = useState(NYXID_CHAT_SERVICE_ID);


  // NyxID Chat: track whether we've ensured the scope binding this session
  const nyxidChatBoundRef = useRef(false);
  const streamingProxyRoomRef = useRef<string | null>(null);

  // Chat
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [debugEvents, setDebugEvents] = useState<RuntimeEvent[]>([]);
  const [showDebug, setShowDebug] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const pendingUserSendRef = useRef<string | null>(null);

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
        { id: NYXID_CHAT_SERVICE_ID, label: 'NyxID Chat', kind: 'nyxid-chat', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
        { id: STREAMING_PROXY_SERVICE_ID, label: 'Streaming Proxy', kind: 'streaming-proxy', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
      ];
      if (Array.isArray(svcList)) {
        const builtinIds = new Set(base.map(b => b.id));
        for (const s of svcList) {
          const sid = s?.serviceId || s?.ServiceId;
          const name = s?.displayName || s?.DisplayName || sid;
          const eps = (s?.endpoints || s?.Endpoints || []).map((ep: any) => ({
            endpointId: ep?.endpointId || ep?.EndpointId || '',
            displayName: ep?.displayName || ep?.DisplayName || ep?.endpointId || '',
            kind: ep?.kind || ep?.Kind || 'command',
          }));
          const kind: ServiceOption['kind'] = isStreamingProxyServiceCandidate(String(sid || ''), String(name || ''), eps)
            ? 'streaming-proxy'
            : 'service';
          const isDuplicate = builtinIds.has(sid)
            || base.some(b => b.label === name);
          if (sid && !isDuplicate) base.push({ id: sid, label: name, kind, endpoints: eps });
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

  // Auto-create new chat when switching services
  const prevServiceRef = useRef(selectedService);
  useEffect(() => {
    if (prevServiceRef.current !== selectedService) {
      prevServiceRef.current = selectedService;
      setMessages([]);
      setDebugEvents([]);
      setActiveConvId(null);
      streamingProxyRoomRef.current = null;
    }
  }, [selectedService]);

  // Reset NyxID Chat binding flag when scope changes
  useEffect(() => {
    nyxidChatBoundRef.current = false;
    streamingProxyRoomRef.current = null;
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
    const actorId = activeService.kind === 'nyxid-chat'
      ? `NyxIdChat:${scopeId}`
      : activeService.kind === 'streaming-proxy'
        ? buildStreamingProxyConversationId(streamingProxyRoomRef.current || genId())
        : `${activeService.id}:${genId()}`;
    // Conversation ID = actor ID
    const convId = activeConvId || actorId;
    const firstUserMsg = msgs.find(m => m.role === 'user');
    const title = (firstUserMsg?.content || 'Untitled').slice(0, 60);
    const now = new Date().toISOString();
    const storedMsgs = msgs
      .filter(m => m.status !== 'streaming')
      .map(m => ({
        id: m.id, role: m.role, content: m.content, timestamp: m.timestamp,
        status: m.status === 'streaming' ? 'complete' : m.status,
        ...(m.authorId ? { authorId: m.authorId } : {}),
        ...(m.authorName ? { authorName: m.authorName } : {}),
        ...(m.error ? { error: m.error } : {}),
        ...(m.thinking ? { thinking: m.thinking } : {}),
      }));
    const meta: ConversationMeta = {
      id: convId,
      actorId,
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

  const ensureStreamingProxyRoom = useCallback(async () => {
    const existingRoomId = streamingProxyRoomRef.current || tryParseStreamingProxyRoomId(activeConvId);
    if (existingRoomId) {
      streamingProxyRoomRef.current = existingRoomId;
      return existingRoomId;
    }

    const created = await api.streamingProxy.createRoom(scopeId, 'Console Chat');
    streamingProxyRoomRef.current = created.roomId;
    return created.roomId;
  }, [scopeId, activeConvId]);

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
    if (activeService.kind === 'streaming-proxy') {
      assistantMsg.authorName = 'Streaming Proxy';
      assistantMsg.content = buildStreamingProxyProgressMessage([], 'starting');
    }

    setMessages(prev => [...prev, userMsg, assistantMsg]);
    setIsStreaming(true);
    setDebugEvents([]);

    const controller = new AbortController();
    abortRef.current = controller;

    const events: RuntimeEvent[] = [];
    const steps: StepInfo[] = [];
    const toolCalls: ToolCallInfo[] = [];
    const joinedParticipants = new Set<string>();
    const progressMessageId = assistantMsg.id;
    const pendingParticipantMessages: ChatMessage[] = [];
    let streamingProxyPhase: 'starting' | 'topic-started' | 'participants-joined' = 'starting';
    let activeProgressMessageId: string | null = progressMessageId;
    let hasParticipantReply = false;
    let displayedParticipantReplyCount = 0;
    let streamFinished = false;
    let pendingStreamError: string | null = null;
    let participantQueueTask: Promise<void> | null = null;
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

    const updateMessageById = (messageId: string | null, patch: Partial<ChatMessage>) => {
      if (!messageId) {
        return;
      }

      setMessages(prev => prev.map(message => (
        message.id === messageId
          ? { ...message, ...patch }
          : message
      )));
    };

    const flushParticipantQueue = async () => {
      if (participantQueueTask) {
        await participantQueueTask;
      }
    };

    const queueParticipantMessage = (message: ChatMessage) => {
      pendingParticipantMessages.push(message);
      if (!participantQueueTask) {
        participantQueueTask = (async () => {
          while (pendingParticipantMessages.length > 0) {
            const nextMessage = pendingParticipantMessages.shift();
            if (!nextMessage) {
              continue;
            }

            const currentProgressMessageId = activeProgressMessageId;

            updateMessageById(currentProgressMessageId, {
              authorName: 'Streaming Proxy',
              status: 'streaming',
              content: buildStreamingProxyTurnMessage(nextMessage.authorName || 'Participant', displayedParticipantReplyCount),
            });

            await sleep(getStreamingProxyRevealDelay(nextMessage.content, displayedParticipantReplyCount));

            if (controller.signal.aborted) {
              pendingParticipantMessages.length = 0;
              break;
            }

            const shouldAppendNextProgress = !streamFinished || pendingParticipantMessages.length > 0;
            const nextProgressId = shouldAppendNextProgress ? genId() : null;
            setMessages(prev => {
              const updated = prev.flatMap(existing => (
                existing.id === currentProgressMessageId
                  ? [{ ...nextMessage, timestamp: Date.now() }]
                  : [existing]
              ));

              if (shouldAppendNextProgress && nextProgressId) {
                updated.push({
                  id: nextProgressId,
                  role: 'assistant',
                  content: buildStreamingProxyWaitingMessage(),
                  authorName: 'Streaming Proxy',
                  timestamp: Date.now(),
                  status: 'streaming',
                });
              }

              return updated;
            });

            activeProgressMessageId = nextProgressId;
            displayedParticipantReplyCount += 1;
          }
        })().finally(() => {
          participantQueueTask = null;
        });
      }
    };

    const onFrame = (frame: any) => {
      const evt = normalizeBackendSseFrame(frame);
      if (!evt) return;
      events.push(evt);
      setDebugEvents([...events]);

      switch (evt.type) {
        case 'TOPIC_STARTED': {
          if (activeService.kind === 'streaming-proxy' && !contentText) {
            streamingProxyPhase = 'topic-started';
            updateMessageById(activeProgressMessageId, { content: buildStreamingProxyProgressMessage(joinedParticipants, streamingProxyPhase) });
          }
          break;
        }

        case 'TEXT_MESSAGE_CONTENT': {
          const delta = String(evt.delta || '');
          contentText += delta;
          if (activeService.kind === 'streaming-proxy') {
            updateMessageById(activeProgressMessageId, { content: contentText });
          } else {
            updateAssistant({ content: contentText });
          }
          break;
        }

        case 'AGENT_MESSAGE': {
          const agentName = String(evt.agentName || evt.agentId || 'Agent');
          const agentContent = String(evt.content || '');
          if (activeService.kind === 'streaming-proxy') {
            hasParticipantReply = true;
            queueParticipantMessage({
              id: genId(),
              role: 'assistant',
              content: agentContent,
              authorId: String(evt.agentId || ''),
              authorName: agentName,
              timestamp: Date.now(),
              status: 'complete',
            });
          } else {
            contentText = agentContent;
            updateAssistant({ content: contentText });
          }
          break;
        }

        case 'PARTICIPANT_JOINED': {
          const displayName = String(evt.displayName || evt.agentId || '').trim();
          if (displayName) joinedParticipants.add(displayName);
          if (activeService.kind === 'streaming-proxy' && !contentText) {
            streamingProxyPhase = 'participants-joined';
            updateMessageById(activeProgressMessageId, { content: buildStreamingProxyProgressMessage(joinedParticipants, streamingProxyPhase) });
          }
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
          const errorText = String(evt.message || 'Unknown error');
          if (activeService.kind === 'streaming-proxy' && (hasParticipantReply || pendingParticipantMessages.length > 0)) {
            pendingStreamError = errorText;
          } else {
            updateMessageById(activeProgressMessageId, { status: 'error', error: errorText });
          }
          break;
        }

        case 'CUSTOM': {
          // Extract text from step completed output
          const stepOutput = extractStepCompletedOutput(evt);
          if (stepOutput && !contentText) {
            // Use step output as the response text if no streaming text was received
            contentText = stepOutput;
            if (activeService.kind === 'streaming-proxy') {
              updateMessageById(activeProgressMessageId, { content: contentText });
            } else {
              updateAssistant({ content: contentText });
            }
            break;
          }

          // Extract reasoning/thinking delta
          const reasoningDelta = extractReasoningDelta(evt);
          if (reasoningDelta) {
            thinking += reasoningDelta;
            if (activeService.kind === 'streaming-proxy') {
              updateMessageById(activeProgressMessageId, { thinking });
            } else {
              updateAssistant({ thinking });
            }
            break;
          }

          // Skip raw observed events (catch-all, not user-facing)
          if (isRawObserved(evt)) break;

          break;
        }
      }
    };

    try {
      if (activeService.kind === 'streaming-proxy') {
        const roomId = await ensureStreamingProxyRoom();
        await api.streamingProxy.streamChat(scopeId, roomId, text, onFrame, controller.signal, activeConvId || undefined);
      } else if (activeService.kind === 'nyxid-chat') {
        // Bind NyxIdChatGAgent as a named service on first use (idempotent PUT).
        // serviceId='nyxid-chat' ensures it doesn't overwrite other bindings.
        // preferredActorId ensures the same actor is reused for conversation history.
        if (!nyxidChatBoundRef.current) {
          await api.scope.bindGAgent(
            scopeId,
            'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent',
            `NyxIdChat:${scopeId}`,
            'NyxID Chat',
            NYXID_CHAT_SERVICE_ID,
          );
          nyxidChatBoundRef.current = true;
        }
        await api.scope.streamInvoke(scopeId, NYXID_CHAT_SERVICE_ID, text, onFrame, controller.signal);
      } else {
        await api.scope.streamInvoke(scopeId, activeService.id, text, onFrame, controller.signal);
      }
      if (activeService.kind === 'streaming-proxy') {
        streamFinished = true;
        await flushParticipantQueue();
      }
      // Only mark complete if no error was already set by RUN_ERROR during streaming.
      setMessages(prev => {
        let updated = [...prev];
        if (activeService.kind === 'streaming-proxy') {
          if (hasParticipantReply) {
            if (activeProgressMessageId) {
              updated = updated.filter(message => message.id !== activeProgressMessageId);
            }
            if (pendingStreamError) {
              updated = [...updated, {
                id: genId(),
                role: 'assistant',
                content: '',
                authorName: 'Streaming Proxy',
                timestamp: Date.now(),
                status: 'error',
                error: pendingStreamError || 'Unknown error',
              }];
            }
          } else {
            updated = updated.map(message => (
              message.id === activeProgressMessageId && message.status !== 'error'
                ? {
                    ...message,
                    status: 'complete',
                    events,
                    steps: [...steps],
                    toolCalls: [...toolCalls],
                    thinking,
                    content: joinedParticipants.size > 0
                      ? `Streaming Proxy 已经把消息发到 room 里了，但当前还没有 participant 回复。已加入: ${Array.from(joinedParticipants).join(', ')}`
                      : 'Streaming Proxy 已经把消息发到 room 里了，但当前没有 participant 回复。它本身不会直接回答，只有 joinRoom/postMessage 的 agent 回消息后，这里才会显示内容。',
                  }
                : message
            ));
          }
        } else {
          const last = updated[updated.length - 1];
          if (last?.role === 'assistant' && last.status !== 'error') {
            updated[updated.length - 1] = {
              ...last,
              status: 'complete',
              events,
              steps: [...steps],
              toolCalls: [...toolCalls],
              thinking,
              content: contentText,
            };
          }
        }
        saveCurrentConversation(updated);
        return updated;
      });
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        const errorText = e?.message || e?.code || JSON.stringify(e);
        if (activeService.kind === 'streaming-proxy' && (hasParticipantReply || pendingParticipantMessages.length > 0)) {
          pendingStreamError = errorText;
          streamFinished = true;
          await flushParticipantQueue();
          setMessages(prev => {
            const withoutProgress = activeProgressMessageId
              ? prev.filter(message => message.id !== activeProgressMessageId)
              : [...prev];
            const updated = [...withoutProgress, {
              id: genId(),
              role: 'assistant' as const,
              content: '',
              authorName: 'Streaming Proxy',
              timestamp: Date.now(),
              status: 'error' as const,
              error: pendingStreamError || 'Unknown error',
            }];
            saveCurrentConversation(updated);
            return updated;
          });
        } else {
          updateMessageById(activeProgressMessageId, {
            status: 'error', error: errorText, events, steps: [...steps], toolCalls: [...toolCalls], thinking,
          });
          setMessages(prev => { saveCurrentConversation(prev); return prev; });
        }
      }
    } finally {
      setIsStreaming(false);
      abortRef.current = null;
    }
  }, [scopeId, isStreaming, activeService, activeConvId, ensureStreamingProxyRoom, saveCurrentConversation]);

  const handleStop = useCallback(() => {
    pendingUserSendRef.current = null;
    abortRef.current?.abort();
  }, []);

  const handleInterruptSend = useCallback((text: string) => {
    if (!text.trim()) {
      return;
    }

    if (!isStreaming) {
      void handleSend(text);
      return;
    }

    pendingUserSendRef.current = text;
    abortRef.current?.abort();
  }, [isStreaming, handleSend]);

  useEffect(() => {
    if (isStreaming) {
      return;
    }

    const queuedText = pendingUserSendRef.current;
    if (!queuedText) {
      return;
    }

    pendingUserSendRef.current = null;
    void handleSend(queuedText);
  }, [isStreaming, handleSend]);

  const handleNewChat = useCallback(() => {
    setMessages([]);
    setDebugEvents([]);
    setActiveConvId(null);
    nyxidChatBoundRef.current = false;
    streamingProxyRoomRef.current = null;
  }, []);

  const handleSelectConversation = useCallback(async (convId: string) => {
    if (!scopeId || convId === activeConvId) return;
    try {
      // Restore the service that was used for this conversation
      const conv = conversations.find(c => c.id === convId);
      if (conv?.serviceId && conv.serviceId !== selectedService) {
        prevServiceRef.current = conv.serviceId; // prevent auto-clear
        setSelectedService(conv.serviceId);
      }
      streamingProxyRoomRef.current = conv?.serviceId === STREAMING_PROXY_SERVICE_ID || conv?.serviceKind === 'streaming-proxy'
        ? tryParseStreamingProxyRoomId(conv.id)
        : null;
      const msgs = await api.chatHistory.getConversation(scopeId, convId);
      setMessages(msgs.map(m => ({
        id: m.id, role: m.role as 'user' | 'assistant', content: m.content,
        authorId: m.authorId ?? undefined,
        authorName: m.authorName ?? undefined,
        timestamp: m.timestamp, status: (m.status || 'complete') as ChatMessage['status'],
        error: m.error ?? undefined, thinking: m.thinking ?? undefined,
      })));
      setActiveConvId(convId);
      setDebugEvents([]);
    } catch { /* ignore */ }
  }, [scopeId, activeConvId, conversations, selectedService]);

  const handleDeleteConversation = useCallback(async (convId: string) => {
    if (!scopeId) return;
    try {
      const roomId = tryParseStreamingProxyRoomId(convId);
      if (roomId) {
        await api.streamingProxy.deleteRoom(scopeId, roomId).catch(() => {});
      }
      await api.chatHistory.deleteConversation(scopeId, convId);
      setConversations(prev => prev.filter(c => c.id !== convId));
      if (activeConvId === convId) {
        setMessages([]);
        setActiveConvId(null);
        setDebugEvents([]);
        streamingProxyRoomRef.current = null;
      }
    } catch { /* ignore */ }
  }, [scopeId, activeConvId]);

  // Endpoint tabs
  type EndpointTab = 'chat' | 'query' | 'execute' | 'raw';
  const [activeEndpoint, setActiveEndpoint] = useState<EndpointTab>('chat');
  const endpointTabs: { id: EndpointTab; label: string }[] = [
    { id: 'chat', label: 'Chat' },
    { id: 'query', label: 'Query' },
    { id: 'execute', label: 'Execute' },
    { id: 'raw', label: 'Raw' },
  ];

  // ── Query tab: inspect scope state ──
  type QueryTarget = 'binding' | 'services' | 'workflows' | 'actor';
  const [queryTarget, setQueryTarget] = useState<QueryTarget>('binding');
  const [queryActorId, setQueryActorId] = useState('');
  const [queryResult, setQueryResult] = useState<string | null>(null);
  const [queryLoading, setQueryLoading] = useState(false);

  const queryTargets: { id: QueryTarget; label: string; description: string }[] = [
    { id: 'binding', label: 'Scope Binding', description: 'Current default service binding for this scope' },
    { id: 'services', label: 'Services', description: 'All services bound to this scope' },
    { id: 'workflows', label: 'Workflows', description: 'Deployed workflows in this scope' },
    { id: 'actor', label: 'Actor Snapshot', description: 'Query a specific actor by ID' },
  ];

  const handleQuerySubmit = async () => {
    setQueryLoading(true);
    setQueryResult(null);
    try {
      let data: any;
      switch (queryTarget) {
        case 'binding':
          data = await api.scope.getBinding(scopeId);
          break;
        case 'services':
          data = await api.scope.listServices(scopeId, 100);
          break;
        case 'workflows':
          data = await api.workspace.listWorkflows();
          break;
        case 'actor':
          if (!queryActorId.trim()) { setQueryLoading(false); return; }
          data = await api.scope.getActorSnapshot(queryActorId.trim());
          break;
      }
      setQueryResult(JSON.stringify(data, null, 2));
    } catch (e: any) {
      setQueryResult(JSON.stringify({ error: e?.message || e }, null, 2));
    } finally {
      setQueryLoading(false);
    }
  };

  // ── Execute tab: invoke service endpoints ──
  const activeEndpoints = activeService?.endpoints ?? [];
  const [invokeEndpointId, setInvokeEndpointId] = useState('chat');
  // Auto-set endpoint when service changes
  useEffect(() => {
    if (activeEndpoints.length > 0 && !activeEndpoints.some(ep => ep.endpointId === invokeEndpointId)) {
      setInvokeEndpointId(activeEndpoints[0].endpointId);
    }
  }, [activeService?.id]); // eslint-disable-line react-hooks/exhaustive-deps
  const [invokeBody, setInvokeBody] = useState('{\n  "prompt": ""\n}');
  const [invokeEvents, setInvokeEvents] = useState<Array<{ type: string; data: any }>>([]);
  const [invokeLoading, setInvokeLoading] = useState(false);
  const invokeAbortRef = useRef<AbortController | null>(null);

  const handleInvokeSubmit = async () => {
    if (!scopeId) return;
    setInvokeLoading(true);
    setInvokeEvents([]);
    const controller = new AbortController();
    invokeAbortRef.current = controller;
    const collected: Array<{ type: string; data: any }> = [];
    try {
      const parsed = JSON.parse(invokeBody);
      const prompt = parsed.prompt || '';
      const serviceId = activeService.kind === 'nyxid-chat' ? NYXID_CHAT_SERVICE_ID : activeService.id;
      const pushFrame = (frame: any) => {
        const evt = normalizeBackendSseFrame(frame);
        if (evt) { collected.push({ type: evt.type, data: evt }); setInvokeEvents([...collected]); }
      };
      if (activeService.kind === 'streaming-proxy') {
        const roomId = await ensureStreamingProxyRoom();
        await api.streamingProxy.streamChat(scopeId, roomId, prompt, pushFrame, controller.signal, activeConvId || undefined);
      } else {
        if (activeService.kind === 'nyxid-chat' && !nyxidChatBoundRef.current) {
          await api.scope.bindGAgent(scopeId, 'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent', `NyxIdChat:${scopeId}`, 'NyxID Chat', NYXID_CHAT_SERVICE_ID);
          nyxidChatBoundRef.current = true;
        }
        await api.scope.streamInvoke(scopeId, serviceId, prompt, pushFrame, controller.signal, invokeEndpointId);
      }
      if (collected.length === 0) collected.push({ type: 'info', data: { message: 'No events received' } });
      setInvokeEvents([...collected]);
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        collected.push({ type: 'ERROR', data: { message: e?.message || JSON.stringify(e) } });
        setInvokeEvents([...collected]);
      }
    } finally {
      setInvokeLoading(false);
      invokeAbortRef.current = null;
    }
  };

  const handleInvokeStop = () => { invokeAbortRef.current?.abort(); };

  // ── Raw tab: API console ──
  const [rawMethod, setRawMethod] = useState('GET');
  const [rawPath, setRawPath] = useState(`/scopes/${scopeId}/binding`);
  const [rawBody, setRawBody] = useState('');
  const [rawResult, setRawResult] = useState<{ status: number; statusText: string; body: string } | null>(null);
  const [rawLoading, setRawLoading] = useState(false);

  const rawShortcuts = [
    { label: 'Binding', path: `/scopes/${scopeId}/binding`, method: 'GET' },
    { label: 'Services', path: `/services?tenantId=${scopeId}&appId=default&namespace=default&take=20`, method: 'GET' },
    { label: 'Workflows', path: `/scopes/${scopeId}/workflows`, method: 'GET' },
    { label: 'GAgent Types', path: `/scopes/gagent-types`, method: 'GET' },
    { label: 'Auth Session', path: `/auth/me`, method: 'GET' },
  ];

  const handleRawSubmit = async () => {
    if (!rawPath.trim()) return;
    setRawLoading(true);
    setRawResult(null);
    try {
      const opts: RequestInit = { method: rawMethod };
      if (rawMethod !== 'GET' && rawBody.trim()) {
        opts.body = rawBody;
        opts.headers = { 'Content-Type': 'application/json' };
      }
      const token = nyxid.getAccessToken();
      if (token) {
        opts.headers = { ...opts.headers as Record<string, string>, Authorization: `Bearer ${token}` };
      }
      const res = await fetch(`/api${rawPath.startsWith('/') ? '' : '/'}${rawPath}`, opts);
      const ct = res.headers.get('content-type') || '';
      const body = ct.includes('json')
        ? JSON.stringify(await res.json(), null, 2)
        : await res.text();
      setRawResult({ status: res.status, statusText: res.statusText, body });
    } catch (e: any) {
      setRawResult({ status: 0, statusText: 'Network Error', body: e?.message || JSON.stringify(e) });
    } finally {
      setRawLoading(false);
    }
  };

  if (!scopeId) {
    return (
      <>
        <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
          <div>
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Console</div>
            <div className="text-[18px] font-bold text-gray-800">Not Logged In</div>
          </div>
        </header>
        <div className="flex-1 flex items-center justify-center text-gray-400 text-[14px]">
          Sign in with NyxID to access the console.
        </div>
      </>
    );
  }

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <header className="flex-shrink-0 border-b border-[#E6E3DE] bg-white/95 backdrop-blur-sm px-5">
        <div className="h-[52px] flex items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <div className="text-[14px] font-semibold text-gray-800">Console</div>
            <ServiceSelector services={services} selected={selectedService} onSelect={setSelectedService} />
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
        </div>

        {/* Endpoint Tabs */}
        <div className="flex items-center gap-1 -mb-px">
          {endpointTabs.map(tab => (
            <button
              key={tab.id}
              onClick={() => setActiveEndpoint(tab.id)}
              className={`px-3 py-1.5 text-[12px] font-medium rounded-t-lg border-b-2 transition-colors ${
                activeEndpoint === tab.id
                  ? 'border-[#18181B] text-gray-800'
                  : 'border-transparent text-gray-400 hover:text-gray-600'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>
      </header>

      {/* Body */}
      {activeEndpoint === 'query' ? (
        /* ── Query: inspect scope state ── */
        <div className="flex-1 min-h-0 overflow-auto bg-[#F2F1EE]">
          <div className="max-w-3xl mx-auto w-full p-6 space-y-5">
            {/* Target selector */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
              {queryTargets.map(t => (
                <button
                  key={t.id}
                  onClick={() => { setQueryTarget(t.id); setQueryResult(null); }}
                  className={`rounded-xl border px-3 py-2.5 text-left transition-all ${
                    queryTarget === t.id
                      ? 'border-[#18181B] bg-white shadow-sm'
                      : 'border-[#E6E3DE] bg-white/60 hover:bg-white'
                  }`}
                >
                  <div className={`text-[12px] font-semibold ${queryTarget === t.id ? 'text-gray-800' : 'text-gray-500'}`}>{t.label}</div>
                  <div className="text-[10px] text-gray-400 mt-0.5 line-clamp-1">{t.description}</div>
                </button>
              ))}
            </div>

            {/* Actor ID input (only for actor target) */}
            {queryTarget === 'actor' && (
              <div className="flex gap-2">
                <input
                  className="flex-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] font-mono text-gray-700 focus:outline-none focus:ring-2 focus:ring-blue-400"
                  placeholder="Enter actor ID..."
                  value={queryActorId}
                  onChange={e => setQueryActorId(e.target.value)}
                  onKeyDown={e => e.key === 'Enter' && handleQuerySubmit()}
                />
              </div>
            )}

            <button
              onClick={handleQuerySubmit}
              disabled={queryLoading || (queryTarget === 'actor' && !queryActorId.trim())}
              className="rounded-lg bg-[#18181B] px-5 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
            >
              {queryLoading ? 'Loading...' : `Query ${queryTargets.find(t => t.id === queryTarget)?.label}`}
            </button>

            {queryResult != null && (
              <div className="rounded-[16px] border border-[#E6E3DE] bg-white overflow-hidden">
                <div className="flex items-center justify-between px-4 py-2 border-b border-[#E6E3DE] bg-[#FAFAF8]">
                  <span className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Result</span>
                  <button
                    onClick={() => navigator.clipboard?.writeText(queryResult)}
                    className="text-[11px] text-gray-400 hover:text-gray-600"
                  >
                    Copy
                  </button>
                </div>
                <pre className="p-4 text-[12px] text-gray-700 font-mono whitespace-pre-wrap overflow-auto max-h-[55vh]">
                  {queryResult}
                </pre>
              </div>
            )}
          </div>
        </div>
      ) : activeEndpoint === 'execute' ? (
        /* ── Execute: invoke service endpoint with streaming ── */
        <div className="flex-1 min-h-0 overflow-auto bg-[#F2F1EE]">
          <div className="max-w-3xl mx-auto w-full p-6 space-y-5">
            <div className="rounded-[16px] border border-[#E6E3DE] bg-white p-4 space-y-3">
              <div className="flex items-center justify-between">
                <div>
                  <div className="text-[13px] font-semibold text-gray-700">Invoke: {activeService.label}</div>
                  <div className="text-[11px] text-gray-400">
                    Endpoint: <span className="font-mono">{invokeEndpointId}:stream</span>
                    {' · Kind: '}<span className="font-mono">{activeService.kind}</span>
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <label className="text-[11px] text-gray-400">Endpoint</label>
                  {activeEndpoints.length > 1 ? (
                    <select
                      className="rounded-md border border-[#E6E3DE] bg-[#FAFAF8] px-2 py-1 text-[12px] font-mono text-gray-600 focus:outline-none focus:ring-1 focus:ring-blue-400"
                      value={invokeEndpointId}
                      onChange={e => setInvokeEndpointId(e.target.value)}
                    >
                      {activeEndpoints.map(ep => (
                        <option key={ep.endpointId} value={ep.endpointId}>
                          {ep.endpointId} ({ep.kind})
                        </option>
                      ))}
                    </select>
                  ) : (
                    <span className="text-[12px] font-mono text-gray-500 bg-[#FAFAF8] border border-[#E6E3DE] rounded-md px-2 py-1">
                      {activeEndpoints[0]?.endpointId || invokeEndpointId}
                    </span>
                  )}
                </div>
              </div>
              <textarea
                rows={6}
                className="w-full rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] px-3 py-2 text-[12px] font-mono text-gray-700 focus:outline-none focus:ring-2 focus:ring-blue-400 resize-y"
                value={invokeBody}
                onChange={e => setInvokeBody(e.target.value)}
                spellCheck={false}
              />
              <div className="flex gap-2">
                <button
                  onClick={handleInvokeSubmit}
                  disabled={invokeLoading}
                  className="rounded-lg bg-[#18181B] px-5 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
                >
                  {invokeLoading ? 'Streaming...' : 'Invoke'}
                </button>
                {invokeLoading && (
                  <button
                    onClick={handleInvokeStop}
                    className="rounded-lg border border-red-300 bg-red-50 px-4 py-2 text-[13px] font-semibold text-red-600 hover:bg-red-100 transition-colors"
                  >
                    Stop
                  </button>
                )}
              </div>
            </div>

            {invokeEvents.length > 0 && (
              <div className="rounded-[16px] border border-[#E6E3DE] bg-white overflow-hidden">
                <div className="flex items-center justify-between px-4 py-2 border-b border-[#E6E3DE] bg-[#FAFAF8]">
                  <span className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">
                    Events ({invokeEvents.length})
                  </span>
                  <button
                    onClick={() => setInvokeEvents([])}
                    className="text-[11px] text-gray-400 hover:text-gray-600"
                  >
                    Clear
                  </button>
                </div>
                <div className="divide-y divide-[#F0EDE8] max-h-[50vh] overflow-auto">
                  {invokeEvents.map((evt, i) => (
                    <div key={i} className="px-4 py-2.5 hover:bg-[#FAFAF8]">
                      <div className="flex items-center gap-2 mb-1">
                        <span className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-bold uppercase tracking-wider ${
                          evt.type === 'ERROR' || evt.type === 'RUN_ERROR'
                            ? 'bg-red-100 text-red-600'
                            : evt.type.includes('CONTENT')
                            ? 'bg-blue-50 text-blue-600'
                            : evt.type.includes('STEP')
                            ? 'bg-amber-50 text-amber-600'
                            : evt.type.includes('TOOL')
                            ? 'bg-violet-50 text-violet-600'
                            : 'bg-gray-100 text-gray-500'
                        }`}>
                          {evt.type}
                        </span>
                        <span className="text-[10px] text-gray-300">#{i + 1}</span>
                      </div>
                      <pre className="text-[11px] text-gray-600 font-mono whitespace-pre-wrap line-clamp-3">
                        {evt.type === 'TEXT_MESSAGE_CONTENT'
                          ? String(evt.data?.delta || '')
                          : JSON.stringify(evt.data, null, 2)}
                      </pre>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      ) : activeEndpoint === 'raw' ? (
        /* ── Raw: API console ── */
        <div className="flex-1 min-h-0 overflow-auto bg-[#F2F1EE]">
          <div className="max-w-3xl mx-auto w-full p-6 space-y-5">
            {/* Shortcuts */}
            <div className="flex flex-wrap gap-1.5">
              {rawShortcuts.map(s => (
                <button
                  key={s.label}
                  onClick={() => { setRawPath(s.path); setRawMethod(s.method); setRawResult(null); }}
                  className={`rounded-full border px-3 py-1 text-[11px] font-medium transition-colors ${
                    rawPath === s.path
                      ? 'border-[#18181B] bg-[#18181B] text-white'
                      : 'border-[#E6E3DE] bg-white text-gray-500 hover:bg-[#FAF8F4]'
                  }`}
                >
                  {s.label}
                </button>
              ))}
            </div>

            {/* Request */}
            <div className="rounded-[16px] border border-[#E6E3DE] bg-white p-4 space-y-3">
              <div className="flex gap-2">
                <select
                  value={rawMethod}
                  onChange={e => setRawMethod(e.target.value)}
                  className="rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] px-2 py-2 text-[12px] font-mono font-semibold text-gray-600 focus:outline-none focus:ring-1 focus:ring-blue-400"
                >
                  <option value="GET">GET</option>
                  <option value="POST">POST</option>
                  <option value="PUT">PUT</option>
                  <option value="DELETE">DELETE</option>
                </select>
                <div className="flex-1 flex items-center gap-0 rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] overflow-hidden">
                  <span className="pl-3 text-[12px] font-mono text-gray-400 select-none">/api</span>
                  <input
                    className="flex-1 bg-transparent px-1 py-2 text-[12px] font-mono text-gray-700 focus:outline-none"
                    value={rawPath}
                    onChange={e => setRawPath(e.target.value)}
                    onKeyDown={e => e.key === 'Enter' && handleRawSubmit()}
                  />
                </div>
                <button
                  onClick={handleRawSubmit}
                  disabled={rawLoading || !rawPath.trim()}
                  className="rounded-lg bg-[#18181B] px-5 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
                >
                  {rawLoading ? '...' : 'Send'}
                </button>
              </div>
              {rawMethod !== 'GET' && (
                <textarea
                  rows={6}
                  className="w-full rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] px-3 py-2 text-[12px] font-mono text-gray-700 focus:outline-none focus:ring-2 focus:ring-blue-400 resize-y"
                  placeholder='{"key": "value"}'
                  value={rawBody}
                  onChange={e => setRawBody(e.target.value)}
                />
              )}
            </div>

            {/* Response */}
            {rawResult != null && (
              <div className="rounded-[16px] border border-[#E6E3DE] bg-white overflow-hidden">
                <div className="flex items-center justify-between px-4 py-2 border-b border-[#E6E3DE] bg-[#FAFAF8]">
                  <div className="flex items-center gap-2">
                    <span className={`inline-block w-2 h-2 rounded-full ${
                      rawResult.status >= 200 && rawResult.status < 300 ? 'bg-green-500' :
                      rawResult.status >= 400 ? 'bg-red-500' : 'bg-amber-500'
                    }`} />
                    <span className="text-[12px] font-mono font-semibold text-gray-600">
                      {rawResult.status} {rawResult.statusText}
                    </span>
                  </div>
                  <button
                    onClick={() => navigator.clipboard?.writeText(rawResult.body)}
                    className="text-[11px] text-gray-400 hover:text-gray-600"
                  >
                    Copy
                  </button>
                </div>
                <pre className="p-4 text-[12px] text-gray-700 font-mono whitespace-pre-wrap overflow-auto max-h-[55vh]">
                  {rawResult.body}
                </pre>
              </div>
            )}
          </div>
        </div>
      ) : (
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
                {activeService.kind === 'nyxid-chat'
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
            onInterruptSend={handleInterruptSend}
            onStop={handleStop}
            isStreaming={isStreaming}
            disabled={!scopeId}
          />
          <div className="mt-1.5 text-center text-[11px] text-gray-300">
            Service: {activeService.kind === 'nyxid-chat' ? 'nyxid-chat' : activeService.id}
            {' · Scope: '}{scopeId.slice(0, 16)}...
          </div>
        </div>
      </div>

        </div>
      </div>
      )}
    </div>
  );
}
