import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { usePretextEstimator, estimateTextareaHeight } from './usePretextEstimator';
import {
  AlignLeft,
  Brain,
  Check,
  ChevronDown,
  Copy as CopyIcon,
  FileText,
  MoreHorizontal,
  Paperclip,
  Pencil,
  RotateCcw,
  Search,
  Trash2,
  X,
} from 'lucide-react';
import {
  normalizeBackendSseFrame,
  extractStepCompletedOutput,
  extractReasoningDelta,
  isRawObserved,
  type RuntimeEvent,
} from './sseUtils';
import { markdownToPlainText, parseMarkdownBlocks, sanitizeAssistantMessageContent, tokenizeInlineContent } from './chatContent';
import type { ChatMessage, ServiceOption, StepInfo, ToolCallInfo, ConversationMeta, AttachmentInfo, ContentPartDto } from './chatTypes';
import { InvokeWorkbench, type InvokeHistoryEntry } from './InvokeWorkbench';
import { parseHumanInputChoices } from './humanInputUtils';
import {
  buildInvokeRequestPayload,
  buildInvokeTransportLabel,
  extractInvokePendingHumanInput,
  getInvokeSurfaceSupport,
  getNonStreamableInvokeEndpoints,
  getStreamableInvokeEndpoints,
  parseInvokeHeaders,
  summarizeInvokeEvents,
} from './invokeUtils';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';
import { NYXID_API_URL } from '../auth/nyxid';
import { createPortal } from 'react-dom';

// ── Constants ──────────────────────────────────────────────────────────────────

const NYXID_CHAT_SERVICE_ID = 'nyxid-chat';
const STREAMING_PROXY_SERVICE_ID = 'streaming-proxy';
const STREAMING_PROXY_FIRST_REPLY_BASE_DELAY_MS = 1500;
const STREAMING_PROXY_BETWEEN_REPLY_BASE_DELAY_MS = 2200;
const STREAMING_PROXY_MAX_REPLY_DELAY_MS = 4200;
const USER_LLM_ROUTE_GATEWAY = '';

/**
 * Onboarding flow — managed entirely in the frontend.
 * Steps: select_provider → (optional) ask_custom_endpoint → ask_api_key → create_service → done
 */
type OnboardingStep = 'select_provider' | 'ask_custom_endpoint' | 'ask_api_key' | 'creating' | 'done' | 'error';
type OnboardingState = {
  step: OnboardingStep;
  slug?: string;
  label?: string;
  endpointUrl?: string;
};

const ONBOARDING_PROVIDER_PROMPT = `No AI service connected. Pick a provider:

  1. Anthropic (Claude)
  2. OpenAI
  3. DeepSeek
  4. Custom OpenAI-compatible endpoint

Enter the number (1-4):`;

const ONBOARDING_CUSTOM_ENDPOINT_PROMPT = 'Enter the API endpoint URL (e.g. https://api.siliconflow.cn/v1):';
const ONBOARDING_API_KEY_PROMPT = 'Enter your API key:';

const PROVIDER_MAP: Record<string, { slug: string; label: string }> = {
  '1': { slug: 'llm-anthropic', label: 'Anthropic (Claude)' },
  '2': { slug: 'llm-openai', label: 'OpenAI' },
  '3': { slug: 'llm-deepseek', label: 'DeepSeek' },
  '4': { slug: 'custom-openai', label: 'Custom OpenAI-compatible' },
};

// Onboarding workflow YAML is in workflows/onboarding.yaml
// (for future use when backend supports keeping SSE alive during workflow suspension).
const USER_CONFIG_PROVIDER_SOURCE_GATEWAY = 'gateway_provider';
const USER_CONFIG_PROVIDER_SOURCE_SERVICE = 'user_service';
const CONVERSATION_ROUTE_DEFAULT_VALUE = '__config_default__';
const CONVERSATION_ROUTE_GATEWAY_VALUE = '__gateway__';
const LLM_ROUTE_HEADER_KEY = 'nyxid.route_preference';
const LLM_MODEL_HEADER_KEY = 'aevatar.model_override';
const BUILTIN_SCOPE_SERVICES: ServiceOption[] = [
  { id: 'onboarding', label: 'Onboarding', kind: 'onboarding', deploymentStatus: 'setup', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
  { id: NYXID_CHAT_SERVICE_ID, label: 'NyxID Chat', kind: 'nyxid-chat', deploymentStatus: 'serving', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
  { id: STREAMING_PROXY_SERVICE_ID, label: 'Streaming Proxy', kind: 'streaming-proxy', deploymentStatus: 'serving', endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }] },
];

// ── Helpers ─────────────────────────────────────────────────────────────────────

type UserConfigProviderStatus = {
  provider_slug: string;
  provider_name: string;
  status: string;
  source?: string;
};

function genId() {
  return crypto.randomUUID?.() ?? Math.random().toString(36).slice(2);
}

function sleep(ms: number) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function normalizeUserLlmRoute(value: unknown): string {
  const normalized = String(value || '').trim();
  if (!normalized || /^auto$/i.test(normalized) || /^gateway$/i.test(normalized)) {
    return USER_LLM_ROUTE_GATEWAY;
  }

  if (normalized.includes('://') || normalized.startsWith('//')) {
    return USER_LLM_ROUTE_GATEWAY;
  }

  if (normalized.startsWith('/')) {
    return normalized;
  }

  return `/api/v1/proxy/s/${normalized.replace(/^\/+|\/+$/g, '')}`;
}

function routePathFromProviderSlug(slug: string) {
  const normalized = String(slug || '').trim();
  return normalized ? `/api/v1/proxy/s/${normalized}` : USER_LLM_ROUTE_GATEWAY;
}

function trimOptional(value: string | undefined) {
  const trimmed = String(value || '').trim();
  return trimmed || undefined;
}

function buildStreamingProxyConversationId(roomId: string) {
  return `${STREAMING_PROXY_SERVICE_ID}:${roomId}`;
}

function tryParseStreamingProxyRoomId(conversationId?: string | null) {
  if (!conversationId) return null;
  const prefix = `${STREAMING_PROXY_SERVICE_ID}:`;
  return conversationId.startsWith(prefix) ? conversationId.slice(prefix.length) : null;
}

function buildConversationHeaders(
  llmRoute: string | undefined,
  llmModel: string | undefined,
) {
  const headers: Record<string, string> = {};
  if (llmRoute !== undefined) {
    headers[LLM_ROUTE_HEADER_KEY] = llmRoute;
  }

  const normalizedModel = trimOptional(llmModel);
  if (normalizedModel) {
    headers[LLM_MODEL_HEADER_KEY] = normalizedModel;
  }

  return Object.keys(headers).length > 0 ? headers : undefined;
}

function encodeRouteSelectValue(route: string | undefined) {
  if (route === undefined) {
    return CONVERSATION_ROUTE_DEFAULT_VALUE;
  }

  return route === USER_LLM_ROUTE_GATEWAY
    ? CONVERSATION_ROUTE_GATEWAY_VALUE
    : route;
}

function decodeRouteSelectValue(value: string) {
  if (value === CONVERSATION_ROUTE_DEFAULT_VALUE) {
    return undefined;
  }

  return value === CONVERSATION_ROUTE_GATEWAY_VALUE
    ? USER_LLM_ROUTE_GATEWAY
    : normalizeUserLlmRoute(value);
}

function describeRoute(route: string | undefined, routeOptions: Array<{ value: string; label: string }>) {
  if (route === undefined) {
    return 'Config default';
  }

  if (route === USER_LLM_ROUTE_GATEWAY) {
    return 'NyxID Gateway';
  }

  return routeOptions.find(option => option.value === route)?.label || route;
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

type LlmModelGroup = {
  id: string;
  label: string;
  models: string[];
};

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
        ? 'scope-chat-link-user'
        : 'scope-chat-link';
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

function getVisibleMessageContent(msg: ChatMessage): string {
  return msg.role === 'assistant'
    ? sanitizeAssistantMessageContent(msg.content)
    : msg.content;
}

function formatQuotedMarkdown(text: string): string {
  return text
    .split('\n')
    .map(line => `> ${line}`)
    .join('\n');
}

function buildMessageCopyText(msg: ChatMessage): string {
  const visibleContent = getVisibleMessageContent(msg).trim();
  if (visibleContent) {
    return visibleContent;
  }

  return buildMessagePlainText(msg);
}

function buildMessageMarkdown(msg: ChatMessage): string {
  const sections: string[] = [];
  const thinking = msg.thinking?.trim();
  const visibleContent = getVisibleMessageContent(msg).trim();
  const error = msg.error?.trim();

  if (thinking) {
    sections.push(`> Thinking\n>\n${formatQuotedMarkdown(thinking)}`);
  }

  if (visibleContent) {
    sections.push(visibleContent);
  }

  if (error) {
    sections.push(`> Error: ${error}`);
  }

  return sections.join('\n\n').trim();
}

function buildMessagePlainText(msg: ChatMessage): string {
  const sections: string[] = [];
  const thinking = msg.thinking?.trim();
  const visibleContent = getVisibleMessageContent(msg).trim();
  const error = msg.error?.trim();

  if (thinking) {
    sections.push(`Thinking\n${thinking}`);
  }

  if (visibleContent) {
    sections.push(markdownToPlainText(visibleContent));
  }

  if (error) {
    sections.push(`Error: ${error}`);
  }

  return sections.join('\n\n').trim();
}

function findPreviousUserMessageIndex(messages: ChatMessage[], startIndex: number): number {
  for (let index = startIndex - 1; index >= 0; index--) {
    if (messages[index]?.role === 'user') {
      return index;
    }
  }

  return -1;
}

async function copyTextToClipboard(text: string): Promise<boolean> {
  const trimmed = text.trim();
  if (!trimmed) {
    return false;
  }

  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(trimmed);
      return true;
    } catch {
      // Fall back to a temporary textarea when async clipboard is unavailable.
    }
  }

  if (typeof document === 'undefined') {
    return false;
  }

  const textarea = document.createElement('textarea');
  textarea.value = trimmed;
  textarea.setAttribute('readonly', 'true');
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  textarea.style.pointerEvents = 'none';
  document.body.appendChild(textarea);
  textarea.select();

  try {
    return document.execCommand('copy');
  } finally {
    document.body.removeChild(textarea);
  }
}

function getThinkingDurationLabel(msg: ChatMessage): string | null {
  if (!msg.thinking?.trim()) {
    return null;
  }

  const allTimestamps = (msg.events ?? [])
    .map(evt => {
      if (typeof evt.timestamp === 'number' && Number.isFinite(evt.timestamp)) {
        return evt.timestamp;
      }

      const numeric = Number(evt.timestamp);
      return Number.isFinite(numeric) ? numeric : null;
    })
    .filter((value): value is number => value !== null);

  const reasoningTimestamps = (msg.events ?? [])
    .filter(evt => evt.type === 'CUSTOM' && evt.name === 'aevatar.llm.reasoning')
    .map(evt => {
      if (typeof evt.timestamp === 'number' && Number.isFinite(evt.timestamp)) {
        return evt.timestamp;
      }

      const numeric = Number(evt.timestamp);
      return Number.isFinite(numeric) ? numeric : null;
    })
    .filter((value): value is number => value !== null);

  const timestamps = reasoningTimestamps.length >= 2 ? reasoningTimestamps : allTimestamps;
  if (timestamps.length < 2) {
    return null;
  }

  const durationMs = Math.max(timestamps[timestamps.length - 1] - timestamps[0], 0);
  return `${(durationMs / 1000).toFixed(3)} 秒`;
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

function ThinkingBlock({
  text,
  isStreaming,
  durationLabel,
}: {
  text: string;
  isStreaming: boolean;
  durationLabel?: string | null;
}) {
  const [open, setOpen] = useState(true);
  if (!text) return null;

  const title = durationLabel
    ? `思考了 ${durationLabel}`
    : isStreaming
      ? '思考中'
      : '思考过程';

  return (
    <div className="mb-4">
      <button
        type="button"
        onClick={() => setOpen(v => !v)}
        className="scope-chat-thinking-toggle flex items-center gap-2 text-[15px] font-semibold transition-colors"
      >
        <Brain size={18} className="shrink-0" />
        <span>{title}</span>
        <ChevronDown size={17} className={`transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && (
        <div className="scope-chat-thinking-body mt-3 ml-4 pl-4 text-[14px] leading-7 whitespace-pre-wrap">
          {text}
        </div>
      )}
    </div>
  );
}

// ── Chat Message Bubble ─────────────────────────────────────────────────────────

type BubbleActionKind = 'copy' | 'markdown' | 'plain' | 'regenerate';

function MessageIconButton(props: {
  title: string;
  active?: boolean;
  emphasis?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      title={props.title}
      onClick={props.onClick}
      className={`scope-chat-action-button flex h-[34px] w-[34px] items-center justify-center rounded-full transition-colors ${
        props.emphasis
          ? 'scope-chat-action-button--emphasis'
          : props.active
            ? 'scope-chat-action-button--active'
            : ''
      }`}
    >
      {props.children}
    </button>
  );
}

function MessageMenuItem(props: {
  icon: ReactNode;
  label: string;
  danger?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={props.onClick}
      className={`scope-chat-menu-item flex w-full items-center gap-3 px-4 py-3 text-left text-[15px] font-medium transition-colors ${
        props.danger
          ? 'scope-chat-menu-item--danger'
          : ''
      }`}
    >
      <span className={props.danger ? 'scope-chat-menu-item-icon-danger' : 'scope-chat-menu-item-icon'}>{props.icon}</span>
      <span>{props.label}</span>
    </button>
  );
}

// ── Media Rendering ──────────────────────────────────────────────────────────

function buildMediaSrc(part: ContentPartDto): string | undefined {
  if (part.uri) {
    // Only allow safe URI schemes
    const lower = part.uri.toLowerCase();
    if (lower.startsWith('https://') || lower.startsWith('http://') || lower.startsWith('data:'))
      return part.uri;
    return undefined; // reject javascript:, etc.
  }
  if (part.dataBase64 && part.mediaType) return `data:${part.mediaType};base64,${part.dataBase64}`;
  if (part.dataBase64) return `data:application/octet-stream;base64,${part.dataBase64}`;
  return undefined;
}

function MediaPartRenderer({ part }: { part: ContentPartDto }) {
  const src = buildMediaSrc(part);
  if (!src) return null;

  switch (part.type) {
    case 'image':
      return (
        <div className="my-2">
          <img src={src} alt={part.name || 'image'} className="max-w-full max-h-[400px] rounded-lg border border-gray-200" />
          {part.name && <div className="text-[11px] text-gray-400 mt-1">{part.name}</div>}
        </div>
      );
    case 'audio':
      return (
        <div className="my-2">
          <audio controls src={src} className="max-w-full" />
          {part.name && <div className="text-[11px] text-gray-400 mt-1">{part.name}</div>}
        </div>
      );
    case 'video':
      return (
        <div className="my-2">
          <video controls src={src} className="max-w-full max-h-[400px] rounded-lg" />
          {part.name && <div className="text-[11px] text-gray-400 mt-1">{part.name}</div>}
        </div>
      );
    default:
      return null;
  }
}

function AttachmentPreview({ att }: { att: AttachmentInfo }) {
  const isImage = att.mediaType.startsWith('image/');
  const isAudio = att.mediaType.startsWith('audio/');
  const isVideo = att.mediaType.startsWith('video/');
  const isPdf = att.mediaType === 'application/pdf';

  if (isImage && att.previewUrl) {
    return (
      <div className="my-1.5">
        <img src={att.previewUrl} alt={att.name} className="max-w-full max-h-[300px] rounded-lg" />
      </div>
    );
  }

  if (isAudio && att.previewUrl) {
    return (
      <div className="my-1.5">
        <audio controls src={att.previewUrl} className="max-w-full" />
      </div>
    );
  }

  if (isVideo && att.previewUrl) {
    return (
      <div className="my-1.5">
        <video controls src={att.previewUrl} className="max-w-full max-h-[300px] rounded-lg" />
      </div>
    );
  }

  if (isPdf && att.previewUrl) {
    return (
      <div className="my-1.5">
        <a href={att.previewUrl} target="_blank" rel="noopener noreferrer"
           className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-gray-200 bg-gray-50 text-[13px] text-blue-600 hover:bg-gray-100">
          <FileText size={14} /> {att.name}
        </a>
      </div>
    );
  }

  return (
    <div className="my-1.5 inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-gray-200 bg-gray-50 text-[13px] text-gray-600">
      <Paperclip size={14} /> {att.name} <span className="text-[11px] text-gray-400">({Math.round(att.size / 1024)}KB)</span>
    </div>
  );
}

function ChatBubble({
  msg,
  canRegenerate,
  canEdit,
  onCopy,
  onCopyMarkdown,
  onCopyPlainText,
  onRegenerate,
  onEdit,
  onDelete,
  onApprove,
  onResumeHumanInput,
}: {
  msg: ChatMessage;
  canRegenerate: boolean;
  canEdit: boolean;
  onCopy: (msg: ChatMessage) => Promise<boolean>;
  onCopyMarkdown: (msg: ChatMessage) => Promise<boolean>;
  onCopyPlainText: (msg: ChatMessage) => Promise<boolean>;
  onRegenerate: () => void;
  onEdit: () => void;
  onDelete: () => void;
  onApprove?: (requestId: string, approved: boolean) => void;
  onResumeHumanInput?: (msg: ChatMessage, userInput: string) => void;
}) {
  const isUser = msg.role === 'user';
  const [stepsOpen, setStepsOpen] = useState(false);
  const [activeAction, setActiveAction] = useState<BubbleActionKind | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const actionTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const menuTriggerRef = useRef<HTMLDivElement | null>(null);
  const [menuPosition, setMenuPosition] = useState<{ top: number; left: number } | null>(null);
  const hasSteps = msg.steps && msg.steps.length > 0;
  const hasTools = msg.toolCalls && msg.toolCalls.length > 0;
  const displayContent = getVisibleMessageContent(msg);
  const thinkingDurationLabel = getThinkingDurationLabel(msg);
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

  useEffect(() => {
    return () => {
      if (actionTimerRef.current) {
        clearTimeout(actionTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    if (!menuOpen) {
      return;
    }

    const handlePointerDown = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) {
        return;
      }

      if (menuRef.current?.contains(event.target) || menuTriggerRef.current?.contains(event.target)) {
        return;
      }

      if (menuRef.current) {
        setMenuOpen(false);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown);
    return () => document.removeEventListener('pointerdown', handlePointerDown);
  }, [menuOpen]);

  useEffect(() => {
    if (!menuOpen) {
      setMenuPosition(null);
      return;
    }

    const updateMenuPosition = () => {
      const trigger = menuTriggerRef.current;
      const menu = menuRef.current;
      if (!trigger || !menu) {
        return;
      }

      const triggerRect = trigger.getBoundingClientRect();
      const menuRect = menu.getBoundingClientRect();
      const viewportPadding = 12;
      const offset = 10;

      let left = triggerRect.right - menuRect.width;
      left = Math.max(viewportPadding, Math.min(left, window.innerWidth - menuRect.width - viewportPadding));

      let top = isUser
        ? triggerRect.bottom + offset
        : triggerRect.top - menuRect.height - offset;
      top = Math.max(viewportPadding, Math.min(top, window.innerHeight - menuRect.height - viewportPadding));

      setMenuPosition({ top, left });
    };

    const rafId = window.requestAnimationFrame(updateMenuPosition);
    window.addEventListener('resize', updateMenuPosition);
    window.addEventListener('scroll', updateMenuPosition, true);

    return () => {
      window.cancelAnimationFrame(rafId);
      window.removeEventListener('resize', updateMenuPosition);
      window.removeEventListener('scroll', updateMenuPosition, true);
    };
  }, [isUser, menuOpen]);

  function showActionFeedback(kind: BubbleActionKind) {
    if (actionTimerRef.current) {
      clearTimeout(actionTimerRef.current);
    }

    setActiveAction(kind);
    actionTimerRef.current = setTimeout(() => {
      setActiveAction(null);
      actionTimerRef.current = null;
    }, 1600);
  }

  async function handleCopyClick(action: BubbleActionKind, handler: (msg: ChatMessage) => Promise<boolean>) {
    const copied = await handler(msg);
    if (copied) {
      showActionFeedback(action);
    }
  }

  function renderMenu() {
    if (typeof document === 'undefined') {
      return null;
    }

    // Portal into .studio-shell (not document.body) to preserve CSS variable scope
    const portalTarget = document.querySelector('.studio-shell') || document.body;

    return createPortal(
      <div
        ref={menuRef}
        className="scope-chat-menu fixed z-[80] w-[236px] overflow-hidden rounded-[22px]"
        style={menuPosition ? { top: menuPosition.top, left: menuPosition.left } : { visibility: 'hidden', top: 0, left: 0 }}
      >
        <div className="py-2">
          <MessageMenuItem
            icon={<FileText size={16} />}
            label="复制为 Markdown"
            onClick={() => {
              setMenuOpen(false);
              void handleCopyClick('markdown', onCopyMarkdown);
            }}
          />
          <MessageMenuItem
            icon={<AlignLeft size={16} />}
            label="复制为纯文本"
            onClick={() => {
              setMenuOpen(false);
              void handleCopyClick('plain', onCopyPlainText);
            }}
          />
        </div>
        <div className="scope-chat-menu-divider py-2">
          <MessageMenuItem
            icon={<Trash2 size={16} />}
            label="删除消息"
            danger
            onClick={() => {
              setMenuOpen(false);
              onDelete();
            }}
          />
        </div>
      </div>,
      portalTarget,
    );
  }

  if (isUser) {
    return (
      <div className="flex flex-col items-end">
        <div className="scope-chat-user-bubble max-w-[80%] rounded-[30px] rounded-br-[14px] px-5 py-3.5 text-[14px] leading-relaxed text-white">
          {msg.attachments?.map(att => (
            <div key={att.id} className="mb-2">
              <AttachmentPreview att={att} />
            </div>
          ))}
          {msg.content && <div className="break-words">{renderContent(msg.content, 'user')}</div>}
        </div>
        <div className="relative mt-2.5 flex items-center gap-1.5">
          <MessageIconButton
            title={activeAction === 'copy' ? '已复制' : '复制消息'}
            active={activeAction === 'copy'}
            onClick={() => { void handleCopyClick('copy', onCopy); }}
          >
            <CopyIcon size={17} />
          </MessageIconButton>
          {canRegenerate && (
            <MessageIconButton
              title="重新生成回复"
              active={activeAction === 'regenerate'}
              onClick={() => {
                onRegenerate();
                showActionFeedback('regenerate');
              }}
            >
              <RotateCcw size={17} />
            </MessageIconButton>
          )}
          {canEdit && (
            <MessageIconButton
              title="编辑消息"
              onClick={() => {
                setMenuOpen(false);
                onEdit();
              }}
            >
              <Pencil size={17} />
            </MessageIconButton>
          )}
          <div ref={menuTriggerRef} className="relative">
            <MessageIconButton
              title="更多操作"
              emphasis
              onClick={() => setMenuOpen(v => !v)}
            >
              <MoreHorizontal size={17} />
            </MessageIconButton>
          </div>
        </div>
        {menuOpen ? renderMenu() : null}
      </div>
    );
  }

  return (
    <div className="flex gap-3 max-w-[94%]">
      <div className={`mt-1 flex h-8 w-8 shrink-0 items-center justify-center rounded-full ${assistantBadgeClass}`}>
        {isParticipantMessage ? (
          <span className="text-[10px] font-semibold tracking-wide">{assistantBadgeLabel}</span>
        ) : (
          <svg className="h-3.5 w-3.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9.75 3.104v5.714a2.25 2.25 0 01-.659 1.591L5 14.5M9.75 3.104c-.251.023-.501.05-.75.082m.75-.082a24.301 24.301 0 014.5 0m0 0v5.714c0 .597.237 1.17.659 1.591L19.8 15.3M14.25 3.104c.251.023.501.05.75.082M19.8 15.3l-1.57.393A9.065 9.065 0 0112 15a9.065 9.065 0 00-6.23.693L5 14.5m14.8.8l1.402 1.402c1.232 1.232.65 3.318-1.067 3.611A48.309 48.309 0 0112 21c-2.773 0-5.491-.235-8.135-.687-1.718-.293-2.3-2.379-1.067-3.61L5 14.5" />
          </svg>
        )}
      </div>

      <div className={`min-w-0 max-w-[85%] flex-1 ${assistantCardClass}`}>
        {msg.thinking && (
          <ThinkingBlock
            text={msg.thinking}
            isStreaming={msg.status === 'streaming'}
            durationLabel={thinkingDurationLabel}
          />
        )}

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

        {(hasSteps || hasTools) && (
          <div className="mb-2">
            <button
              type="button"
              onClick={() => setStepsOpen(v => !v)}
              className="flex items-center gap-1.5 text-[12px] text-gray-400 hover:text-gray-600 py-1"
            >
              <ChevronDown size={15} className={`transition-transform ${stepsOpen ? 'rotate-180' : ''}`} />
              <span>
                {(msg.steps?.length || 0) + (msg.toolCalls?.length || 0)} action{((msg.steps?.length || 0) + (msg.toolCalls?.length || 0)) > 1 ? 's' : ''}
              </span>
              {(msg.steps?.some(s => s.status === 'running') || msg.toolCalls?.some(t => t.status === 'running')) && (
                <span className="inline-block h-1.5 w-1.5 rounded-full bg-amber-400 animate-pulse" />
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

        <div className="text-[14px] leading-[2.05] text-gray-900">
          <div className="break-words">
            {renderContent(displayContent, 'assistant')}
            {msg.status === 'streaming' && displayContent && (
              <span className="ml-0.5 inline-block h-[18px] w-[2px] animate-blink align-text-bottom bg-gray-400" />
            )}
          </div>
          {msg.mediaParts?.map((part, i) => (
            <MediaPartRenderer key={`media-${i}`} part={part} />
          ))}
          {!displayContent && !msg.mediaParts?.length && msg.status === 'streaming' && (
            <div className="flex items-center gap-1.5 py-2">
              <span className="block h-1.5 w-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '0ms' }} />
              <span className="block h-1.5 w-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '200ms' }} />
              <span className="block h-1.5 w-1.5 rounded-full bg-gray-300 animate-bounce" style={{ animationDelay: '400ms' }} />
            </div>
          )}
        </div>

        {msg.status === 'error' && msg.error && (
          <div className="mt-3 flex items-start gap-2 rounded-2xl border border-red-200 bg-red-50 px-3 py-2.5">
            <svg className="mt-0.5 h-4 w-4 shrink-0 text-red-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z" />
            </svg>
            <span className="text-[13px] text-red-600">{msg.error}</span>
          </div>
        )}

        {msg.pendingHumanInput && (() => {
          const parsed = parseHumanInputChoices(msg.pendingHumanInput.prompt, msg.pendingHumanInput.options);
          const hasChoices = parsed.choices.length >= 2;

          return hasChoices ? (
            <div className="mt-3 rounded-2xl border border-blue-200 bg-blue-50 px-4 py-3">
              <div className="mb-2.5 flex items-center gap-2">
                <svg className="h-4 w-4 text-blue-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span className="text-[13px] font-medium text-blue-700">Choose an option</span>
              </div>
              {parsed.questionText && (
                <div className="mb-3 whitespace-pre-wrap text-[13px] text-blue-600">{parsed.questionText}</div>
              )}
              <div className="flex flex-col gap-1.5">
                {parsed.choices.map(choice => (
                  <button
                    key={choice.key}
                    onClick={() => onResumeHumanInput?.(msg, choice.value)}
                    className="flex w-full items-center gap-2.5 rounded-xl border border-blue-200 bg-white px-3 py-2 text-left text-[13px] text-gray-700 transition-colors hover:border-blue-300 hover:bg-blue-100"
                  >
                    <span className="flex h-6 w-6 flex-shrink-0 items-center justify-center rounded-lg bg-blue-100 text-[12px] font-semibold text-blue-600">
                      {choice.key}
                    </span>
                    <span>{choice.label}</span>
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <div className="mt-3 rounded-2xl border border-blue-200 bg-blue-50 px-4 py-3">
              <div className="flex items-center gap-2">
                <svg className="h-4 w-4 animate-pulse text-blue-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.087.16 2.185.283 3.293.369V21l4.076-4.076a1.526 1.526 0 011.037-.443 48.282 48.282 0 005.68-.494c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0012 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018z" />
                </svg>
                <span className="text-[13px] text-blue-600">Waiting for your input — type your answer below</span>
              </div>
            </div>
          );
        })()}

        {msg.pendingApproval && (
          <div className="mt-3 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3">
            <div className="mb-2 flex items-center gap-2">
              <svg className="h-4 w-4 text-amber-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126z" />
              </svg>
              <span className="text-[13px] font-medium text-amber-700">Tool Approval Required</span>
            </div>
            <div className="mb-3 text-[12px] text-amber-600">
              <span className="rounded bg-amber-100 px-1.5 py-0.5 font-mono">{msg.pendingApproval.toolName}</span>
              {msg.pendingApproval.isDestructive && (
                <span className="ml-2 text-[11px] text-red-500">destructive</span>
              )}
            </div>
            <div className="flex gap-2">
              <button
                onClick={() => onApprove?.(msg.pendingApproval!.requestId, true)}
                className="rounded-lg bg-green-500 px-3 py-1.5 text-[12px] font-medium text-white transition-colors hover:bg-green-600"
              >
                Approve
              </button>
              <button
                onClick={() => onApprove?.(msg.pendingApproval!.requestId, false)}
                className="rounded-lg bg-gray-200 px-3 py-1.5 text-[12px] font-medium text-gray-700 transition-colors hover:bg-gray-300"
              >
                Deny
              </button>
            </div>
          </div>
        )}

        <div className="relative mt-3.5 flex items-center gap-1.5">
          <MessageIconButton
            title={activeAction === 'copy' ? '已复制' : '复制回复'}
            active={activeAction === 'copy'}
            onClick={() => { void handleCopyClick('copy', onCopy); }}
          >
            <CopyIcon size={17} />
          </MessageIconButton>
          {canRegenerate && (
            <MessageIconButton
              title="重新生成回复"
              active={activeAction === 'regenerate'}
              onClick={() => {
                onRegenerate();
                showActionFeedback('regenerate');
              }}
            >
              <RotateCcw size={17} />
            </MessageIconButton>
          )}
          <div ref={menuTriggerRef} className="relative">
            <MessageIconButton
              title="更多操作"
              emphasis
              onClick={() => setMenuOpen(v => !v)}
            >
              <MoreHorizontal size={17} />
            </MessageIconButton>
          </div>
        </div>
        {menuOpen ? renderMenu() : null}
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

function ConversationLlmConfigBar({
  mode = 'conversation',
  routeValue,
  routeOptions,
  modelValue,
  modelGroups,
  effectiveRoute,
  effectiveRouteLabel,
  effectiveModel,
  modelsLoading,
  disabled,
  onRouteChange,
  onModelChange,
  onReset,
}: {
  mode?: 'conversation' | 'streaming-proxy';
  routeValue: string | undefined;
  routeOptions: Array<{ value: string; label: string }>;
  modelValue: string | undefined;
  modelGroups: LlmModelGroup[];
  effectiveRoute: string;
  effectiveRouteLabel: string;
  effectiveModel: string;
  modelsLoading: boolean;
  disabled?: boolean;
  onRouteChange: (value: string | undefined) => void;
  onModelChange: (value: string | undefined) => void;
  onReset: () => void;
}) {
  const hasOverride = routeValue !== undefined || !!trimOptional(modelValue);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const [panelPosition, setPanelPosition] = useState<{ top: number; left: number; height: number; width: number } | null>(null);
  const selectedModel = trimOptional(modelValue) || effectiveModel;
  const routeSelectValue = encodeRouteSelectValue(routeValue);
  const normalizedQuery = query.trim().toLowerCase();
  const isStreamingProxyMode = mode === 'streaming-proxy';
  const panelTitle = isStreamingProxyMode ? 'Room model preferences' : 'Conversation model';
  const routeLabel = isStreamingProxyMode ? 'Preferred route' : 'Route';
  const defaultRouteLabel = isStreamingProxyMode ? 'Room default' : 'Config default';
  const emptyStateLabel = isStreamingProxyMode
    ? `No room models for ${effectiveRouteLabel}`
    : `No models for ${effectiveRouteLabel}`;
  const inlineRouteLabel = isStreamingProxyMode
    ? `room prefers ${effectiveRouteLabel}`
    : effectiveRoute === USER_LLM_ROUTE_GATEWAY
      ? effectiveRouteLabel
      : `via ${effectiveRouteLabel}`;
  const filteredGroups = modelGroups
    .map(group => ({
      ...group,
      models: normalizedQuery
        ? group.models.filter(model => model.toLowerCase().includes(normalizedQuery))
        : group.models,
    }))
    .filter(group => group.models.length > 0);
  const exactModelMatch = Boolean(
    query.trim()
      && modelGroups.some(group => group.models.some(model => model.toLowerCase() === normalizedQuery)),
  );

  useEffect(() => {
    if (!open) {
      setPanelPosition(null);
      setQuery('');
      return;
    }

    const updatePanelPosition = () => {
      const trigger = triggerRef.current;
      if (!trigger) {
        return;
      }

      const triggerRect = trigger.getBoundingClientRect();
      const viewportPadding = 12;
      const offset = 12;
      const preferredHeight = 460;
      const preferredWidth = 380;
      const panelWidth = Math.min(preferredWidth, window.innerWidth - viewportPadding * 2);
      const spaceAbove = Math.max(0, triggerRect.top - viewportPadding - offset);
      const spaceBelow = Math.max(0, window.innerHeight - triggerRect.bottom - viewportPadding - offset);
      const panelHeight = Math.min(preferredHeight, window.innerHeight - viewportPadding * 2);
      const preferAbove = spaceAbove >= panelHeight || spaceAbove >= spaceBelow;

      let left = triggerRect.left;
      left = Math.max(viewportPadding, Math.min(left, window.innerWidth - panelWidth - viewportPadding));

      let top = preferAbove
        ? triggerRect.top - panelHeight - offset
        : triggerRect.bottom + offset;
      top = Math.max(viewportPadding, Math.min(top, window.innerHeight - panelHeight - viewportPadding));

      setPanelPosition({ top, left, height: panelHeight, width: panelWidth });
    };

    const handlePointerDown = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) {
        return;
      }

      if (triggerRef.current?.contains(event.target) || panelRef.current?.contains(event.target)) {
        return;
      }

      setOpen(false);
    };

    const rafId = window.requestAnimationFrame(updatePanelPosition);
    document.addEventListener('pointerdown', handlePointerDown);
    window.addEventListener('resize', updatePanelPosition);
    window.addEventListener('scroll', updatePanelPosition, true);

    return () => {
      window.cancelAnimationFrame(rafId);
      document.removeEventListener('pointerdown', handlePointerDown);
      window.removeEventListener('resize', updatePanelPosition);
      window.removeEventListener('scroll', updatePanelPosition, true);
    };
  }, [open]);

  const handleModelSelect = (nextModel: string | undefined) => {
    onModelChange(trimOptional(nextModel));
    setOpen(false);
    setQuery('');
  };

  const renderPanel = () => {
    if (!open || typeof document === 'undefined') {
      return null;
    }

    return createPortal(
      <div
        ref={panelRef}
        className="scope-chat-llm-panel fixed z-[90] w-[380px] max-w-[calc(100vw-24px)] overflow-hidden"
        style={panelPosition ? {
          top: panelPosition.top,
          left: panelPosition.left,
          height: panelPosition.height,
          width: panelPosition.width,
        } : { visibility: 'hidden', top: 0, left: 0 }}
      >
        <div className="scope-chat-llm-panel-header">
          <div className="scope-chat-llm-panel-title">{panelTitle}</div>
          {hasOverride ? (
            <button
              type="button"
              onClick={() => {
                onReset();
                setOpen(false);
              }}
              className="scope-chat-llm-reset"
            >
              Reset
            </button>
          ) : null}
        </div>
        {isStreamingProxyMode ? (
          <div className="px-4 pb-2 text-[12px] text-[#8A877F]">
            Used to rank room participants, not to direct-chat a single node.
          </div>
        ) : null}

        <div className="scope-chat-llm-search">
          <Search size={15} className="scope-chat-llm-search-icon" />
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Enter' && query.trim()) {
                event.preventDefault();
                handleModelSelect(query.trim());
              }
            }}
            placeholder={modelsLoading ? 'Loading models...' : 'Search models...'}
            className="scope-chat-llm-search-input"
          />
        </div>

        <div className="scope-chat-llm-route-row">
          <span className="scope-chat-llm-route-label">{routeLabel}</span>
          <select
            value={routeSelectValue}
            onChange={event => onRouteChange(decodeRouteSelectValue(event.target.value))}
            className="scope-chat-llm-route-select"
          >
            <option value={CONVERSATION_ROUTE_DEFAULT_VALUE}>{defaultRouteLabel}</option>
            {routeOptions.map(option => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="scope-chat-llm-options">
          {query.trim() && !exactModelMatch ? (
            <button
              type="button"
              onClick={() => handleModelSelect(query.trim())}
              className="scope-chat-llm-option scope-chat-llm-option--manual"
            >
              <div className="scope-chat-llm-option-main">
                <Check size={15} className="opacity-0" />
                <span>Use “{query.trim()}”</span>
              </div>
              <span className="scope-chat-llm-option-badge">Manual</span>
            </button>
          ) : null}

          {!modelsLoading && filteredGroups.length === 0 ? (
            <div className="scope-chat-llm-empty">{emptyStateLabel}</div>
          ) : null}

          {filteredGroups.map(group => (
            <div key={group.id} className="scope-chat-llm-group">
              <div className="scope-chat-llm-group-label">{group.label}</div>
              {group.models.map(model => {
                const isActive = selectedModel === model;
                return (
                  <button
                    key={model}
                    type="button"
                    onClick={() => handleModelSelect(model)}
                    className={`scope-chat-llm-option ${isActive ? 'is-active' : ''}`}
                  >
                    <div className="scope-chat-llm-option-main">
                      <Check size={15} className={isActive ? 'opacity-100' : 'opacity-0'} />
                      <span>{model}</span>
                    </div>
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      </div>,
      document.body,
    );
  };

  return (
    <div className="scope-chat-llm-bar">
      <button
        ref={triggerRef}
        type="button"
        disabled={disabled}
        onClick={() => setOpen(value => !value)}
        className="scope-chat-llm-trigger"
      >
        <span className="scope-chat-llm-trigger-label">{selectedModel || 'Provider default'}</span>
        <ChevronDown
          size={15}
          className={`scope-chat-llm-chevron shrink-0 transition-transform ${open ? 'rotate-180' : ''}`}
        />
      </button>
      <span className="scope-chat-llm-inline-route">{inlineRouteLabel}</span>
      {renderPanel()}
    </div>
  );
}

// ── Chat Input ──────────────────────────────────────────────────────────────────

function ChatInput({
  value,
  onChange,
  onSend,
  onStop,
  isStreaming,
  disabled,
  focusToken,
  footer,
  pendingAttachments,
  onAttach,
  onRemoveAttachment,
}: {
  value: string;
  onChange: (text: string) => void;
  onSend: (text: string, attachments?: AttachmentInfo[]) => void;
  onStop: () => void;
  isStreaming: boolean;
  disabled: boolean;
  focusToken: number;
  footer?: ReactNode;
  pendingAttachments?: AttachmentInfo[];
  onAttach?: (files: FileList) => void;
  onRemoveAttachment?: (id: string) => void;
}) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const canSend = (value.trim() || (pendingAttachments && pendingAttachments.length > 0)) && !isStreaming && !disabled;

  const handleSend = useCallback(() => {
    if (!canSend) return;
    onSend(value.trim(), pendingAttachments);
    if (textareaRef.current) textareaRef.current.style.height = 'auto';
  }, [value, canSend, onSend, pendingAttachments]);

  // Pretext-powered textarea height (no DOM reflow)
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    // Use content width for estimation (minus px-4 = 32px padding)
    const widthPx = el.clientWidth || 600;
    const estimated = estimateTextareaHeight(value, widthPx);
    el.style.height = estimated + 'px';
  }, [value]);

  useEffect(() => {
    if (!focusToken) return;
    textareaRef.current?.focus();
  }, [focusToken]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const handleInput = () => {
    const el = textareaRef.current;
    if (!el) return;
    const widthPx = el.clientWidth || 600;
    const estimated = estimateTextareaHeight(value, widthPx);
    el.style.height = estimated + 'px';
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files.length > 0 && onAttach) {
      onAttach(e.target.files);
    }
    e.target.value = '';
  };

  return (
    <div className="relative">
      <div className="rounded-2xl border border-[#E6E3DE] bg-white shadow-sm focus-within:ring-2 focus-within:ring-blue-400 focus-within:border-transparent">
        {/* Attachment preview bar */}
        {pendingAttachments && pendingAttachments.length > 0 && (
          <div className="flex flex-wrap gap-2 px-4 pt-3">
            {pendingAttachments.map(att => (
              <div key={att.id} className="relative group flex items-center gap-1.5 rounded-lg border border-gray-200 bg-gray-50 px-2 py-1.5 text-[12px] text-gray-600">
                {att.mediaType.startsWith('image/') && att.previewUrl ? (
                  <img src={att.previewUrl} alt={att.name} className="h-8 w-8 rounded object-cover" />
                ) : (
                  <span className="text-[16px]">
                    {att.mediaType.startsWith('audio/') ? '\u{1F3B5}' :
                     att.mediaType.startsWith('video/') ? '\u{1F3AC}' :
                     att.mediaType === 'application/pdf' ? '\u{1F4C4}' : '\u{1F4CE}'}
                  </span>
                )}
                <span className="max-w-[120px] truncate">{att.name}</span>
                {onRemoveAttachment && (
                  <button
                    onClick={() => onRemoveAttachment(att.id)}
                    className="ml-0.5 rounded-full p-0.5 hover:bg-gray-200 transition-colors"
                  >
                    <X size={12} />
                  </button>
                )}
              </div>
            ))}
          </div>
        )}
        <div className="flex items-end">
          <textarea
            ref={textareaRef}
            rows={1}
            className="min-h-[82px] flex-1 resize-none bg-transparent px-4 pt-4 pb-3 text-[14px] focus:outline-none placeholder:text-gray-400"
            value={value}
            onChange={e => { onChange(e.target.value); handleInput(); }}
            onKeyDown={handleKeyDown}
            placeholder="Send a message..."
            disabled={disabled}
          />
          <div className="flex-shrink-0 flex items-center gap-1 p-2">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              accept="image/*,audio/*,video/*,.pdf,.md"
              className="hidden"
              onChange={handleFileChange}
            />
            {!isStreaming && (
              <button
                onClick={() => fileInputRef.current?.click()}
                disabled={disabled}
                className="w-8 h-8 flex items-center justify-center rounded-lg text-gray-400 hover:text-gray-600 hover:bg-gray-100 disabled:opacity-20 transition-colors"
                title="Attach file"
              >
                <Paperclip size={16} />
              </button>
            )}
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
                disabled={!canSend}
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
        {footer ? (
          <div className="flex items-center gap-3 px-4 pb-3">
            {footer}
          </div>
        ) : null}
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
                isActive ? 'border-l-2' : 'hover:bg-[#F7F5F2] border-l-2 border-l-transparent'
              }`}
              style={isActive ? { background: 'var(--accent-soft-end)', borderLeftColor: 'var(--accent)' } : undefined}
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
  const [services, setServices] = useState<ServiceOption[]>(BUILTIN_SCOPE_SERVICES);
  const [selectedService, setSelectedService] = useState(NYXID_CHAT_SERVICE_ID);


  // ── Onboarding: frontend-driven state machine ──
  const [onboardingState, setOnboardingState] = useState<OnboardingState | null>(null);

  // NyxID Chat: track whether we've ensured the scope binding this session
  const nyxidChatBoundRef = useRef(false);
  const streamingProxyRoomRef = useRef<string | null>(null);

  // Chat
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [debugEvents, setDebugEvents] = useState<RuntimeEvent[]>([]);
  const [showDebug, setShowDebug] = useState(false);
  const [composerText, setComposerText] = useState('');
  const [composerFocusToken, setComposerFocusToken] = useState(0);
  const [pendingAttachments, setPendingAttachments] = useState<AttachmentInfo[]>([]);
  const abortRef = useRef<AbortController | null>(null);
  const scrollParentRef = useRef<HTMLDivElement>(null);
  const isAtBottomRef = useRef(true);
  const [chatContainerWidth, setChatContainerWidth] = useState(700);
  const pretextEstimate = usePretextEstimator();

  // Chat history persistence
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [activeConvId, setActiveConvId] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [globalDefaultModel, setGlobalDefaultModel] = useState('');
  const [globalPreferredRoute, setGlobalPreferredRoute] = useState(USER_LLM_ROUTE_GATEWAY);
  const [llmProviders, setLlmProviders] = useState<UserConfigProviderStatus[]>([]);
  const [modelsByProvider, setModelsByProvider] = useState<Record<string, string[]>>({});
  const [modelsLoading, setModelsLoading] = useState(false);
  const [conversationRoute, setConversationRoute] = useState<string | undefined>(undefined);
  const [conversationModel, setConversationModel] = useState<string | undefined>(undefined);

  // Load services on mount
  useEffect(() => {
    if (!scopeId) return;
    api.scope.listServices(scopeId).then(svcList => {
      const base: ServiceOption[] = [...BUILTIN_SCOPE_SERVICES];
      if (Array.isArray(svcList)) {
        const builtinIds = new Set(base.map(b => b.id));
        for (const s of svcList) {
          const sid = s?.serviceId || s?.ServiceId;
          const name = s?.displayName || s?.DisplayName || sid;
          const eps = (s?.endpoints || s?.Endpoints || []).map((ep: any) => ({
            endpointId: ep?.endpointId || ep?.EndpointId || '',
            displayName: ep?.displayName || ep?.DisplayName || ep?.endpointId || '',
            kind: ep?.kind || ep?.Kind || 'command',
            requestTypeUrl: ep?.requestTypeUrl || ep?.RequestTypeUrl || '',
            responseTypeUrl: ep?.responseTypeUrl || ep?.ResponseTypeUrl || '',
            description: ep?.description || ep?.Description || '',
          }));
          const kind: ServiceOption['kind'] = isStreamingProxyServiceCandidate(String(sid || ''), String(name || ''), eps)
            ? 'streaming-proxy'
            : 'service';
          const isDuplicate = builtinIds.has(sid)
            || base.some(b => b.label === name);
          if (sid && !isDuplicate) {
            base.push({
              id: sid,
              label: name,
              kind,
              endpoints: eps,
              deploymentStatus: String(s?.deployment_status || s?.deploymentStatus || s?.DeploymentStatus || ''),
              serviceKey: String(s?.service_key || s?.serviceKey || s?.ServiceKey || ''),
            });
          }
        }
      }
      setServices(base);
    }).catch(() => {});
  }, [scopeId]);

  useEffect(() => {
    if (!scopeId) return;
    let cancelled = false;
    setModelsLoading(true);

    (async () => {
      try {
        const [userConfigData, modelData] = await Promise.all([
          api.userConfig.get(),
          api.userConfig.models(),
        ]);
        if (cancelled) return;
        setGlobalDefaultModel(trimOptional(userConfigData?.defaultModel) || '');
        setGlobalPreferredRoute(normalizeUserLlmRoute(userConfigData?.preferredLlmRoute));
        setLlmProviders(modelData?.providers ?? []);
        setModelsByProvider(modelData?.models_by_provider ?? {});
      } catch {
        if (cancelled) return;
        setGlobalDefaultModel('');
        setGlobalPreferredRoute(USER_LLM_ROUTE_GATEWAY);
        setLlmProviders([]);
        setModelsByProvider({});
      } finally {
        if (!cancelled) {
          setModelsLoading(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [scopeId]);

  // ── Virtualizer for chat messages ──
  const chatVirtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => scrollParentRef.current,
    estimateSize: (index) => pretextEstimate(messages[index], chatContainerWidth),
    overscan: 5,
    getItemKey: (index) => messages[index].id,
  });

  // Track container width for Pretext estimation
  useEffect(() => {
    const el = scrollParentRef.current;
    if (!el) return;
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        // max-w-3xl = 768px, minus px-4 (32px) padding
        const w = Math.min(entry.contentRect.width, 768) - 32;
        if (w > 0) setChatContainerWidth(w);
      }
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // Stick-to-bottom: auto-scroll when at bottom
  const handleChatScroll = useCallback(() => {
    const el = scrollParentRef.current;
    if (!el) return;
    isAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  }, []);

  // Auto-scroll on new messages or content changes (streaming)
  useLayoutEffect(() => {
    if (messages.length > 0 && isAtBottomRef.current) {
      chatVirtualizer.scrollToIndex(messages.length - 1, { align: 'end', behavior: 'smooth' });
    }
  }, [messages, messages.length > 0 ? messages[messages.length - 1].content.length : 0]);

  const activeService = services.find(s => s.id === selectedService) || services[0];
  const readyProviders = useMemo(
    () => llmProviders.filter(provider => provider.status === 'ready'),
    [llmProviders],
  );
  const gatewayProviders = useMemo(
    () => readyProviders.filter(provider => (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) === USER_CONFIG_PROVIDER_SOURCE_GATEWAY),
    [readyProviders],
  );
  const serviceProviders = useMemo(
    () => llmProviders.filter(provider => provider.source === USER_CONFIG_PROVIDER_SOURCE_SERVICE),
    [llmProviders],
  );
  const routeOptions = useMemo(() => {
    const options: Array<{ value: string; label: string }> = [
      { value: USER_LLM_ROUTE_GATEWAY, label: 'NyxID Gateway' },
    ];
    const seen = new Set(options.map(option => option.value));

    for (const provider of serviceProviders) {
      const route = routePathFromProviderSlug(provider.provider_slug);
      if (!provider.provider_slug || seen.has(route)) continue;
      seen.add(route);
      options.push({
        value: route,
        label: provider.provider_name || provider.provider_slug,
      });
    }

    for (const route of [globalPreferredRoute, conversationRoute]) {
      if (route && !seen.has(route)) {
        seen.add(route);
        options.push({ value: route, label: route });
      }
    }

    return options;
  }, [conversationRoute, globalPreferredRoute, serviceProviders]);
  const effectiveRoute = conversationRoute !== undefined ? conversationRoute : globalPreferredRoute;
  const effectiveRouteLabel = describeRoute(effectiveRoute, routeOptions);
  const effectiveModel = trimOptional(conversationModel) || globalDefaultModel;
  const modelGroups = useMemo<LlmModelGroup[]>(() => {
    const providerGroup = effectiveRoute === USER_LLM_ROUTE_GATEWAY
      ? gatewayProviders
      : llmProviders.filter(provider => routePathFromProviderSlug(provider.provider_slug) === effectiveRoute);
    const groups: LlmModelGroup[] = providerGroup
      .map(provider => {
        const models = Array.from(new Set((modelsByProvider[provider.provider_slug] || []).filter(Boolean)));
        return {
          id: provider.provider_slug || provider.provider_name,
          label: provider.provider_name || provider.provider_slug,
          models,
        };
      })
      .filter(group => group.models.length > 0);

    const selected = trimOptional(conversationModel) || trimOptional(globalDefaultModel);
    if (selected && !groups.some(group => group.models.includes(selected))) {
      groups.unshift({
        id: '__current__',
        label: 'Current',
        models: [selected],
      });
    }

    return groups;
  }, [conversationModel, effectiveRoute, gatewayProviders, globalDefaultModel, llmProviders, modelsByProvider]);
  const conversationHeaders = useMemo(
    () => buildConversationHeaders(conversationRoute, conversationModel),
    [conversationModel, conversationRoute],
  );

  // Auto-create new chat when switching services
  const prevServiceRef = useRef(selectedService);
  useEffect(() => {
    if (prevServiceRef.current !== selectedService) {
      prevServiceRef.current = selectedService;
      setMessages([]);
      setDebugEvents([]);
      setActiveConvId(null);
      streamingProxyRoomRef.current = null;
      setComposerText('');
      setConversationRoute(undefined);
      setConversationModel(undefined);
    }
  }, [selectedService]);

  // Reset NyxID Chat binding flag when scope changes
  useEffect(() => {
    nyxidChatBoundRef.current = false;
    streamingProxyRoomRef.current = null;
    setOnboardingState(null);
  }, [scopeId]);

  // Load conversation index when scope changes
  useEffect(() => {
    if (!scopeId) return;
    api.chatHistory.getIndex(scopeId).then(data => {
      setConversations(data?.conversations ?? []);
    }).catch(() => {});
  }, [scopeId]);

  // Save current conversation to chrono-storage (called after streaming completes)
  const saveCurrentConversation = useCallback((msgs: ChatMessage[], actorIdOverride?: string) => {
    const actorId = trimOptional(actorIdOverride)
      || trimOptional(activeConvId ?? undefined)
      || (
        activeService.kind === 'streaming-proxy' && streamingProxyRoomRef.current
          ? buildStreamingProxyConversationId(streamingProxyRoomRef.current)
          : undefined
      );
    if (!scopeId || msgs.length === 0 || !actorId) return;
    const convId = actorId;
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
        ...(m.attachments?.length ? { attachments: m.attachments.map(({ file, previewUrl, ...rest }) => rest) } : {}),
        ...(m.mediaParts?.length ? { mediaParts: m.mediaParts } : {}),
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
      ...(conversationRoute !== undefined ? { llmRoute: conversationRoute } : {}),
      ...(trimOptional(conversationModel) ? { llmModel: trimOptional(conversationModel) } : {}),
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
  }, [scopeId, activeConvId, activeService, conversationModel, conversationRoute, conversations]);

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

  const persistConversationOverrides = useCallback((
    nextRoute: string | undefined,
    nextModel: string | undefined,
  ) => {
    if (!scopeId || !activeConvId || isStreaming || messages.length === 0) {
      return;
    }

    const now = new Date().toISOString();
    const existing = conversations.find(conv => conv.id === activeConvId);
    const firstUserMsg = messages.find(message => message.role === 'user');
    const title = (existing?.title || firstUserMsg?.content || 'Untitled').slice(0, 60);
    const storedMsgs = messages
      .filter(message => message.status !== 'streaming')
      .map(message => ({
        id: message.id,
        role: message.role,
        content: message.content,
        timestamp: message.timestamp,
        status: message.status === 'streaming' ? 'complete' : message.status,
        ...(message.authorId ? { authorId: message.authorId } : {}),
        ...(message.authorName ? { authorName: message.authorName } : {}),
        ...(message.error ? { error: message.error } : {}),
        ...(message.thinking ? { thinking: message.thinking } : {}),
        ...(message.attachments?.length ? { attachments: message.attachments.map(({ file, previewUrl, ...rest }) => rest) } : {}),
        ...(message.mediaParts?.length ? { mediaParts: message.mediaParts } : {}),
      }));
    const meta: ConversationMeta = {
      id: activeConvId,
      actorId: existing?.actorId,
      title,
      serviceId: existing?.serviceId || activeService.id,
      serviceKind: existing?.serviceKind || activeService.kind,
      createdAt: existing?.createdAt || now,
      updatedAt: now,
      messageCount: storedMsgs.length,
      ...(nextRoute !== undefined ? { llmRoute: nextRoute } : {}),
      ...(trimOptional(nextModel) ? { llmModel: trimOptional(nextModel) } : {}),
    };

    setConversations(prev => [meta, ...prev.filter(conv => conv.id !== activeConvId)]);
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      api.chatHistory.saveConversation(scopeId, activeConvId, meta, storedMsgs).catch(() => {});
    }, 300);
  }, [scopeId, activeConvId, isStreaming, messages, conversations, activeService]);

  const handleConversationRouteChange = useCallback((value: string | undefined) => {
    setConversationRoute(value);
    persistConversationOverrides(value, conversationModel);
  }, [conversationModel, persistConversationOverrides]);

  const handleConversationModelChange = useCallback((value: string | undefined) => {
    const normalized = trimOptional(value);
    setConversationModel(normalized);
    persistConversationOverrides(conversationRoute, normalized);
  }, [conversationRoute, persistConversationOverrides]);

  const handleResetConversationLlm = useCallback(() => {
    setConversationRoute(undefined);
    setConversationModel(undefined);
    persistConversationOverrides(undefined, undefined);
  }, [persistConversationOverrides]);

  // ── Send / regenerate message ──
  const streamChatTurn = useCallback(async (
    text: string,
    options?: {
      baseMessages?: ChatMessage[];
      includeUserMessage?: boolean;
      attachments?: AttachmentInfo[];
      inputParts?: ContentPartDto[];
    },
  ) => {
    if (!scopeId || isStreaming) return;

    const includeUserMessage = options?.includeUserMessage ?? true;
    const baseMessages = options?.baseMessages ?? messages;
    const userMsg = includeUserMessage
      ? {
          id: genId(),
          role: 'user' as const,
          content: text,
          timestamp: Date.now(),
          status: 'complete' as const,
          attachments: options?.attachments,
        }
      : null;
    const assistantMsg: ChatMessage = {
      id: genId(), role: 'assistant', content: '', timestamp: Date.now(), status: 'streaming',
      steps: [], toolCalls: [], thinking: '',
    };
    if (activeService.kind === 'streaming-proxy') {
      assistantMsg.authorName = 'Streaming Proxy';
      assistantMsg.content = buildStreamingProxyProgressMessage([], 'starting');
    }
    const nextMessages = userMsg
      ? [...baseMessages, userMsg, assistantMsg]
      : [...baseMessages, assistantMsg];

    setMessages(nextMessages);
    setIsStreaming(true);
    setDebugEvents([]);

    const controller = new AbortController();
    abortRef.current = controller;

    const events: RuntimeEvent[] = [];
    const steps: StepInfo[] = [];
    const toolCalls: ToolCallInfo[] = [];
    let resolvedActorId = trimOptional(activeConvId ?? undefined) || '';
    let thinking = '';
    let contentText = '';
    let lastWasReasoning = false;
    const joinedParticipants = new Map<string, string>();
    const progressMessageId = assistantMsg.id;
    const pendingParticipantMessages: ChatMessage[] = [];
    let streamingProxyPhase: 'starting' | 'topic-started' | 'participants-joined' = 'starting';
    let activeProgressMessageId: string | null = progressMessageId;
    let hasParticipantReply = false;
    let displayedParticipantReplyCount = 0;
    let streamFinished = false;
    let pendingStreamError: string | null = null;
    let participantQueueTask: Promise<void> | null = null;

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

    const updateCurrentAssistant = (patch: Partial<ChatMessage>) => {
      if (activeService.kind === 'streaming-proxy') {
        updateMessageById(activeProgressMessageId, patch);
        return;
      }

      updateAssistant(patch);
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

      if (!resolvedActorId && evt.type === 'RUN_STARTED' && typeof evt.threadId === 'string' && evt.threadId.trim()) {
        resolvedActorId = evt.threadId.trim();
        setActiveConvId(prev => prev || resolvedActorId);
      }

      const wasReasoning = lastWasReasoning;
      lastWasReasoning = false;

      switch (evt.type) {
        case 'TOPIC_STARTED': {
          if (activeService.kind === 'streaming-proxy' && !contentText) {
            streamingProxyPhase = 'topic-started';
            updateMessageById(activeProgressMessageId, {
              content: buildStreamingProxyProgressMessage(joinedParticipants.values(), streamingProxyPhase),
            });
          }
          break;
        }

        case 'TEXT_MESSAGE_CONTENT': {
          const delta = String(evt.delta || '');
          contentText += delta;
          updateCurrentAssistant({ content: contentText });
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
            updateCurrentAssistant({ content: contentText });
          }
          break;
        }

        case 'PARTICIPANT_JOINED': {
          const agentId = String(evt.agentId || '').trim();
          const displayName = String(evt.displayName || evt.agentId || '').trim();
          if (agentId && displayName) joinedParticipants.set(agentId, displayName);
          if (activeService.kind === 'streaming-proxy' && !contentText) {
            streamingProxyPhase = 'participants-joined';
            updateMessageById(activeProgressMessageId, {
              content: buildStreamingProxyProgressMessage(joinedParticipants.values(), streamingProxyPhase),
            });
          }
          break;
        }

        case 'PARTICIPANT_LEFT': {
          const agentId = String(evt.agentId || '').trim();
          if (agentId) joinedParticipants.delete(agentId);
          if (activeService.kind === 'streaming-proxy' && !contentText) {
            streamingProxyPhase = 'participants-joined';
            updateMessageById(activeProgressMessageId, {
              content: buildStreamingProxyProgressMessage(joinedParticipants.values(), streamingProxyPhase),
            });
          }
          break;
        }

        case 'STEP_STARTED': {
          const stepName = String(evt.stepName || '');
          steps.push({ name: stepName, status: 'running', startedAt: Date.now() });
          updateCurrentAssistant({ steps: [...steps] });
          break;
        }

        case 'STEP_FINISHED': {
          const stepName = String(evt.stepName || '');
          const existing = steps.find(s => s.name === stepName && s.status === 'running');
          if (existing) {
            existing.status = 'done';
            existing.finishedAt = Date.now();
          }
          updateCurrentAssistant({ steps: [...steps] });
          break;
        }

        case 'TOOL_CALL_START': {
          const toolName = String(evt.toolName || '');
          const toolCallId = String(evt.toolCallId || '');
          toolCalls.push({ id: toolCallId, name: toolName, status: 'running' });
          updateCurrentAssistant({ toolCalls: [...toolCalls] });
          break;
        }

        case 'TOOL_CALL_END': {
          const toolCallId = String(evt.toolCallId || '');
          const existing = toolCalls.find(t => t.id === toolCallId && t.status === 'running');
          if (existing) {
            existing.status = 'done';
            existing.result = String(evt.result || '');
          }
          updateCurrentAssistant({ toolCalls: [...toolCalls] });
          break;
        }

        case 'TOOL_APPROVAL_REQUEST': {
          updateCurrentAssistant({
            pendingApproval: {
              requestId: String(evt.requestId || ''),
              toolName: String(evt.toolName || ''),
              toolCallId: String(evt.toolCallId || ''),
              argumentsJson: String(evt.argumentsJson || ''),
              isDestructive: !!evt.isDestructive,
              timeoutSeconds: typeof evt.timeoutSeconds === 'number' ? evt.timeoutSeconds : 15,
            },
          });
          break;
        }

        case 'MEDIA_CONTENT': {
          const mediaPart: ContentPartDto = {
            type: (String(evt.kind || 'image')) as ContentPartDto['type'],
            dataBase64: evt.dataBase64 ? String(evt.dataBase64) : undefined,
            mediaType: evt.mediaType ? String(evt.mediaType) : undefined,
            uri: evt.uri ? String(evt.uri) : undefined,
            name: evt.name ? String(evt.name) : undefined,
            text: evt.text ? String(evt.text) : undefined,
          };
          setMessages(prev => {
            const updated = [...prev];
            const last = updated[updated.length - 1];
            if (last?.role === 'assistant') {
              updated[updated.length - 1] = {
                ...last,
                mediaParts: [...(last.mediaParts || []), mediaPart],
              };
            }
            return updated;
          });
          break;
        }

        case 'HUMAN_INPUT_REQUEST': {
          const prompt = String(evt.prompt || '');
          const rawOpts = evt.options;
          const options = Array.isArray(rawOpts) ? rawOpts.filter((o): o is string => typeof o === 'string') : undefined;
          setIsStreaming(false);
          updateCurrentAssistant({
            content: prompt || 'Waiting for your input...',
            status: 'complete',
            pendingHumanInput: {
              stepId: String(evt.stepId || ''),
              runId: String(evt.runId || ''),
              prompt,
              serviceId: activeService.id,
              actorId: trimOptional(activeConvId ?? undefined) || undefined,
              ...(options && options.length > 0 ? { options } : {}),
            },
          });
          break;
        }

        case 'RUN_ERROR': {
          const errorText = String(evt.message || 'Unknown error');
          if (activeService.kind === 'streaming-proxy' && (hasParticipantReply || pendingParticipantMessages.length > 0)) {
            pendingStreamError = errorText;
          } else {
            updateCurrentAssistant({ status: 'error', error: errorText });
          }
          break;
        }

        case 'CUSTOM': {
          // TOOL_APPROVAL_REQUEST arrives as CUSTOM from the generic AGUI endpoint.
          // Payload is a protobuf Any wrapping a Struct, so fields may be under .value
          if (String(evt.name || '') === 'TOOL_APPROVAL_REQUEST') {
            const raw = (evt.payload ?? evt.value ?? {}) as any;
            const p = (raw?.value ?? raw) as any;
            updateCurrentAssistant({
              pendingApproval: {
                requestId: String(p.requestId ?? p.request_id ?? ''),
                toolName: String(p.toolName ?? p.tool_name ?? ''),
                toolCallId: String(p.toolCallId ?? p.tool_call_id ?? ''),
                argumentsJson: String(p.argumentsJson ?? p.arguments_json ?? ''),
                isDestructive: !!(p.isDestructive ?? p.is_destructive),
                timeoutSeconds: typeof p.timeoutSeconds === 'number' ? p.timeoutSeconds
                  : typeof p.timeout_seconds === 'number' ? p.timeout_seconds : 15,
              },
            });
            break;
          }

          // human_input request arrives as CUSTOM from workflow endpoints
          if (String(evt.name || '') === 'aevatar.human_input.request') {
            const raw = (evt.payload ?? evt.value ?? {}) as any;
            const p = (raw?.value ?? raw) as any;
            const prompt = String(p?.prompt ?? p?.Prompt ?? '');
            const rawOpts = p?.options ?? p?.Options;
            const options = Array.isArray(rawOpts) ? rawOpts.filter((o: unknown): o is string => typeof o === 'string') : undefined;
            setIsStreaming(false);
            updateCurrentAssistant({
              content: prompt || 'Waiting for your input...',
              status: 'complete',
              pendingHumanInput: {
                stepId: String(p?.stepId ?? p?.step_id ?? ''),
                runId: String(p?.runId ?? p?.run_id ?? ''),
                prompt,
                serviceId: activeService.id,
                actorId: trimOptional(activeConvId ?? undefined) || undefined,
                ...(options && options.length > 0 ? { options } : {}),
              },
            });
            break;
          }

          const stepOutput = extractStepCompletedOutput(evt);
          if (stepOutput && !contentText) {
            contentText = stepOutput;
            updateCurrentAssistant({ content: contentText });
            break;
          }

          const reasoningDelta = extractReasoningDelta(evt);
          if (reasoningDelta) {
            if (thinking && !wasReasoning) {
              thinking += '\n\n';
            }
            thinking += reasoningDelta;
            lastWasReasoning = true;
            updateCurrentAssistant({ thinking });
            break;
          }

          if (isRawObserved(evt)) break;

          break;
        }
      }
    };

    try {
      const requestActorId = trimOptional(activeConvId ?? undefined) || undefined;
      if (activeService.kind === 'streaming-proxy') {
        const roomId = await ensureStreamingProxyRoom();
        resolvedActorId = buildStreamingProxyConversationId(roomId);
        await api.streamingProxy.streamChat(
          scopeId,
          roomId,
          text,
          onFrame,
          controller.signal,
          activeConvId || undefined,
          conversationRoute,
          conversationModel,
        );
        streamFinished = true;
        await flushParticipantQueue();
      } else if (activeService.kind === 'nyxid-chat') {
        if (!nyxidChatBoundRef.current) {
          await api.scope.bindGAgent(
            scopeId,
            'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent',
            'NyxID Chat',
            NYXID_CHAT_SERVICE_ID,
          );
          nyxidChatBoundRef.current = true;
        }
        await api.scope.streamInvoke(scopeId, NYXID_CHAT_SERVICE_ID, text, onFrame, controller.signal, 'chat', conversationHeaders, requestActorId, options?.inputParts);
      } else {
        await api.scope.streamInvoke(scopeId, activeService.id, text, onFrame, controller.signal, 'chat', conversationHeaders, requestActorId, options?.inputParts);
      }

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
                      ? `Streaming Proxy 已经把消息发到 room 里了，但当前还没有 participant 回复。已加入: ${Array.from(joinedParticipants.values()).join(', ')}`
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
        saveCurrentConversation(updated, resolvedActorId);
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
            saveCurrentConversation(updated, resolvedActorId);
            return updated;
          });
        } else {
          updateCurrentAssistant({
            status: 'error', error: errorText, events, steps: [...steps], toolCalls: [...toolCalls], thinking,
          });
          setMessages(prev => {
            saveCurrentConversation(prev, resolvedActorId);
            return prev;
          });
        }
      }
    } finally {
      setIsStreaming(false);
      abortRef.current = null;
    }
  }, [scopeId, isStreaming, messages, activeService, activeConvId, conversationHeaders, saveCurrentConversation, ensureStreamingProxyRoom]);

  const handleAttach = useCallback((files: FileList) => {
    const maxSize = 50 * 1024 * 1024; // 50 MB
    const newAttachments: AttachmentInfo[] = Array.from(files)
      .filter(file => file.size <= maxSize)
      .map(file => {
        const id = crypto.randomUUID();
        const ext = file.name.split('.').pop() || 'bin';
        return {
          id,
          name: file.name,
          mediaType: file.type || 'application/octet-stream',
          size: file.size,
          storageKey: `chat-media/${id}.${ext}`,
          previewUrl: URL.createObjectURL(file),
          file,
        };
      });
    setPendingAttachments(prev => [...prev, ...newAttachments]);
  }, []);

  const handleRemoveAttachment = useCallback((id: string) => {
    setPendingAttachments(prev => {
      const removed = prev.find(a => a.id === id);
      if (removed?.previewUrl) URL.revokeObjectURL(removed.previewUrl);
      return prev.filter(a => a.id !== id);
    });
  }, []);

  const handleResumeHumanInput = useCallback(async (targetMsg: ChatMessage, userInput: string) => {
    if (!scopeId || isStreaming) return;
    const hi = targetMsg.pendingHumanInput;
    if (!hi) return;

    const serviceId = hi.serviceId || services.find(s => s.id === selectedService)?.id;
    if (!serviceId || !hi.runId) return;

    // Add user message and clear pending state
    const userMsg: ChatMessage = {
      id: genId(), role: 'user', content: userInput,
      timestamp: Date.now(), status: 'complete',
    };
    const assistantMsg: ChatMessage = {
      id: genId(), role: 'assistant', content: '', timestamp: Date.now(), status: 'streaming',
    };

    setMessages(prev => {
      const updated = prev.map(m =>
        m.id === targetMsg.id ? { ...m, pendingHumanInput: undefined } : m,
      );
      return [...updated, userMsg, assistantMsg];
    });
    setIsStreaming(true);
    setDebugEvents([]);

    const controller = new AbortController();
    abortRef.current = controller;

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

    try {
      await api.scope.resumeRun(
        scopeId,
        serviceId,
        hi.runId,
        {
          stepId: hi.stepId,
          userInput,
          approved: true,
          actorId: hi.actorId,
        },
      );

      // resumeRun is a POST (not SSE) — continuation comes via the existing SSE stream
      updateAssistant({ status: 'complete' });
    } catch (err) {
      const errorMsg = err instanceof Error ? err.message : 'Resume failed';
      updateAssistant({ status: 'error', error: errorMsg });
    } finally {
      setIsStreaming(false);
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
    }
  }, [scopeId, isStreaming, services, selectedService]);

  const handleSend = useCallback(async (text: string, attachments?: AttachmentInfo[]) => {
    setComposerText('');
    const atts = attachments || pendingAttachments;
    // Don't revoke object URLs — they're still needed for display in sent messages.
    // Cleanup happens on conversation switch or component unmount.
    setPendingAttachments([]);

    // ── Onboarding state machine ──
    if (onboardingState && selectedService === 'onboarding') {
      const userMsg: ChatMessage = {
        id: `user-${Date.now()}`, role: 'user', content: text,
        timestamp: Date.now(), status: 'complete',
      };

      if (onboardingState.step === 'select_provider') {
        const trimmed = text.trim();
        const provider = PROVIDER_MAP[trimmed] ?? PROVIDER_MAP['2']; // default to OpenAI
        const isCustom = trimmed === '4';
        const nextStep: OnboardingStep = isCustom ? 'ask_custom_endpoint' : 'ask_api_key';
        const nextPrompt = isCustom ? ONBOARDING_CUSTOM_ENDPOINT_PROMPT : ONBOARDING_API_KEY_PROMPT;
        setOnboardingState({ step: nextStep, slug: provider.slug, label: provider.label });
        setMessages(prev => [...prev, userMsg, {
          id: `onboarding-${Date.now()}`, role: 'assistant', content: nextPrompt,
          timestamp: Date.now(), status: 'complete',
        }]);
        return;
      }

      if (onboardingState.step === 'ask_custom_endpoint') {
        setOnboardingState(prev => prev ? { ...prev, step: 'ask_api_key', endpointUrl: text.trim() } : prev);
        setMessages(prev => [...prev, userMsg, {
          id: `onboarding-${Date.now()}`, role: 'assistant', content: ONBOARDING_API_KEY_PROMPT,
          timestamp: Date.now(), status: 'complete',
        }]);
        return;
      }

      if (onboardingState.step === 'ask_api_key' && scopeId) {
        const apiKey = text.trim();
        setOnboardingState(prev => prev ? { ...prev, step: 'creating' } : prev);
        setMessages(prev => [...prev, userMsg, {
          id: `onboarding-creating-${Date.now()}`, role: 'assistant', content: 'Configuring your provider...',
          timestamp: Date.now(), status: 'streaming',
        }]);
        // Call NyxID API to create the service via the scope proxy
        const body = {
          service_slug: onboardingState.slug,
          credential: apiKey,
          label: onboardingState.label,
          ...(onboardingState.endpointUrl ? { endpoint_url: onboardingState.endpointUrl } : {}),
        };
        void (async () => {
          try {
            // Use the NyxIdChat's tool endpoint or direct proxy. For now, use the scope's
            // streamInvoke with nyxid-chat to create the service via the agent.
            // Simplest: POST directly to the NyxID proxy through the backend.
            const token = nyxid.getAccessToken();
            const resp = await fetch(`${NYXID_API_URL}/api/v1/keys`, {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                ...(token ? { Authorization: `Bearer ${token}` } : {}),
              },
              body: JSON.stringify(body),
            });
            if (!resp.ok) {
              const errText = await resp.text().catch(() => resp.statusText);
              throw new Error(errText || `HTTP ${resp.status}`);
            }
            setOnboardingState({ step: 'done' });
            setMessages(prev => {
              const updated = [...prev];
              const last = updated[updated.length - 1];
              if (last?.role === 'assistant' && last.status === 'streaming') {
                updated[updated.length - 1] = {
                  ...last,
                  content: `Connected! ${onboardingState.label} is ready.\nYou can switch to NyxID Chat to start using it.`,
                  status: 'complete',
                };
              }
              return updated;
            });
          } catch (err: any) {
            setOnboardingState(prev => prev ? { ...prev, step: 'ask_api_key' } : prev);
            setMessages(prev => {
              const updated = [...prev];
              const last = updated[updated.length - 1];
              if (last?.role === 'assistant') {
                updated[updated.length - 1] = {
                  ...last,
                  content: `Connection failed: ${err?.message || 'Unknown error'}\n\nPlease enter a valid API key:`,
                  status: 'complete',
                };
              }
              return updated;
            });
          }
        })();
        return;
      }

      // done or error — reset onboarding on any further input
      if (onboardingState.step === 'done') {
        setOnboardingState({ step: 'select_provider' });
        setMessages(prev => [...prev, userMsg, {
          id: `onboarding-${Date.now()}`, role: 'assistant', content: ONBOARDING_PROVIDER_PROMPT,
          timestamp: Date.now(), status: 'complete',
        }]);
        return;
      }
    }

    // ── Resume suspended workflow if pendingHumanInput is active ──
    const lastAssistant = [...messages].reverse().find((m: ChatMessage) => m.role === 'assistant');
    if (lastAssistant?.pendingHumanInput) {
      void handleResumeHumanInput(lastAssistant, text);
      return;
    }

    // Build inputParts from attachments
    const inputParts: ContentPartDto[] = [];
    if (atts.length > 0) {
      for (const att of atts) {
        const fileRef = att.file;
        if (!fileRef) continue;

        // Upload to chrono-storage for persistence
        try {
          await api.explorer.uploadFile(att.storageKey, fileRef);
        } catch (err) {
          console.warn('Upload to chrono-storage failed, falling back to inline base64:', err);
        }

        // Only send media types the LLM can handle as inline base64
        const isMedia = att.mediaType.startsWith('image/') || att.mediaType.startsWith('audio/') || att.mediaType.startsWith('video/');
        if (!isMedia) continue; // Skip PDF/markdown for inline LLM input

        // Convert to base64 ContentPart for LLM
        const base64 = await new Promise<string>((resolve, reject) => {
          const reader = new FileReader();
          reader.onload = () => {
            const result = reader.result as string;
            resolve(result.split(',')[1] || '');
          };
          reader.onerror = () => reject(reader.error);
          reader.readAsDataURL(fileRef);
        });
        const kind: ContentPartDto['type'] = att.mediaType.startsWith('image/') ? 'image'
          : att.mediaType.startsWith('audio/') ? 'audio'
          : 'video';
        inputParts.push({ type: kind, dataBase64: base64, mediaType: att.mediaType, name: att.name });
      }
    }

    void streamChatTurn(text, { baseMessages: messages, includeUserMessage: true, attachments: atts, inputParts: inputParts.length > 0 ? inputParts : undefined });
  }, [messages, streamChatTurn, handleResumeHumanInput, scopeId, onboardingState, selectedService, pendingAttachments]);

  const handleRegenerate = useCallback((messageIndex: number) => {
    if (isStreaming) return;

    const userIndex = messages[messageIndex]?.role === 'user'
      ? messageIndex
      : findPreviousUserMessageIndex(messages, messageIndex);
    if (userIndex < 0) return;

    const prompt = messages[userIndex]?.content.trim();
    if (!prompt) return;

    const baseMessages = messages.slice(0, userIndex + 1);
    void streamChatTurn(prompt, { baseMessages, includeUserMessage: false });
  }, [isStreaming, messages, streamChatTurn]);

  const handleDeleteMessage = useCallback(async (messageIndex: number) => {
    const nextMessages = messages.slice(0, messageIndex);
    setMessages(nextMessages);
    setDebugEvents([]);

    if (nextMessages.length === 0) {
      if (scopeId && activeConvId) {
        try {
          const roomId = tryParseStreamingProxyRoomId(activeConvId);
          if (roomId) {
            await api.streamingProxy.deleteRoom(scopeId, roomId).catch(() => {});
          }
          await api.chatHistory.deleteConversation(scopeId, activeConvId);
        } catch {
          // Ignore delete failures and still clear local state.
        }
      }
      setActiveConvId(null);
      streamingProxyRoomRef.current = null;
      setConversationRoute(undefined);
      setConversationModel(undefined);
      setConversations(prev => prev.filter(conv => conv.id !== activeConvId));
      return;
    }

    saveCurrentConversation(nextMessages);
  }, [messages, scopeId, activeConvId, saveCurrentConversation]);

  const handleEditMessage = useCallback((messageIndex: number) => {
    const message = messages[messageIndex];
    if (!message || message.role !== 'user') {
      return;
    }

    abortRef.current?.abort();
    abortRef.current = null;
    setIsStreaming(false);
    setComposerText(message.content);
    setComposerFocusToken(token => token + 1);
    setMessages(messages.slice(0, messageIndex));
    setDebugEvents([]);
    setActiveConvId(null);
    streamingProxyRoomRef.current = null;
  }, [messages]);

  const handleCopyMessage = useCallback((msg: ChatMessage) => {
    return copyTextToClipboard(buildMessageCopyText(msg));
  }, []);

  const handleCopyMessageMarkdown = useCallback((msg: ChatMessage) => {
    return copyTextToClipboard(buildMessageMarkdown(msg));
  }, []);

  const handleCopyMessagePlainText = useCallback((msg: ChatMessage) => {
    return copyTextToClipboard(buildMessagePlainText(msg));
  }, []);

  const handleToolApproval = useCallback(async (requestId: string, approved: boolean) => {
    if (!scopeId) return;
    const activeService = services.find(s => s.id === selectedService);
    if (!activeService) return;
    if (activeService.kind !== 'nyxid-chat') return;
    const targetMessage = messages.find(message =>
      message.role === 'assistant' && message.pendingApproval?.requestId === requestId,
    );
    if (!targetMessage) return;

    const actorId = trimOptional(activeConvId ?? undefined);
    if (!actorId) return;
    const targetMessageId = targetMessage.id;

    const clearPendingApproval = (items: ChatMessage[]) =>
      items.map(message =>
        message.pendingApproval?.requestId === requestId
          ? { ...message, pendingApproval: undefined }
          : message,
      );

    if (!approved) {
      setMessages(prev => {
        const updated = clearPendingApproval(prev);
        saveCurrentConversation(updated);
        return updated;
      });
      return;
    }
    const controller = new AbortController();
    abortRef.current = controller;
    setIsStreaming(true);
    setDebugEvents([]);
    setMessages(prev => prev.map(message =>
      message.id === targetMessageId
        ? { ...message, pendingApproval: undefined, status: 'streaming' }
        : message,
    ));

    const events: RuntimeEvent[] = [...(targetMessage.events ?? [])];
    const toolCalls: ToolCallInfo[] = (targetMessage.toolCalls ?? []).map(toolCall => ({ ...toolCall }));
    let contentText = targetMessage.content;

    const updateApprovalAssistant = (patch: Partial<ChatMessage>) => {
      setMessages(prev => prev.map(message =>
        message.id === targetMessageId
          ? { ...message, ...patch }
          : message,
      ));
    };

    const finalizeApprovalAssistant = (status: ChatMessage['status'], error?: string) => {
      setMessages(prev => {
        const updated = prev.map(message => {
          if (message.id !== targetMessageId) {
            return message;
          }

          const nextStatus = status === 'complete' && message.status === 'error'
            ? 'error'
            : status;
          return {
            ...message,
            status: nextStatus,
            ...(error ? { error } : {}),
            events,
            toolCalls: [...toolCalls],
          };
        });
        saveCurrentConversation(updated);
        return updated;
      });
    };

    try {
      await api.nyxidChat.approveToolCall(
        scopeId,
        actorId,
        requestId,
        true,
        (frame: any) => {
          const evt = normalizeBackendSseFrame(frame);
          if (!evt) return;

          events.push(evt);
          setDebugEvents([...events]);

          switch (evt.type) {
            case 'TEXT_MESSAGE_CONTENT': {
              contentText += String(evt.delta || '');
              updateApprovalAssistant({ content: contentText });
              break;
            }
            case 'TOOL_CALL_START': {
              toolCalls.push({
                id: String(evt.toolCallId || ''),
                name: String(evt.toolName || ''),
                status: 'running',
              });
              updateApprovalAssistant({ toolCalls: [...toolCalls] });
              break;
            }
            case 'TOOL_CALL_END': {
              const callId = String(evt.toolCallId || '');
              const existing = toolCalls.find(toolCall => toolCall.id === callId && toolCall.status === 'running');
              if (existing) {
                existing.status = 'done';
                existing.result = String(evt.result || '');
              }
              updateApprovalAssistant({ toolCalls: [...toolCalls] });
              break;
            }
            case 'TOOL_APPROVAL_REQUEST': {
              updateApprovalAssistant({
                pendingApproval: {
                  requestId: String(evt.requestId || ''),
                  toolName: String(evt.toolName || ''),
                  toolCallId: String(evt.toolCallId || ''),
                  argumentsJson: String(evt.argumentsJson || ''),
                  isDestructive: !!evt.isDestructive,
                  timeoutSeconds: typeof evt.timeoutSeconds === 'number' ? evt.timeoutSeconds : 15,
                },
              });
              break;
            }
            case 'RUN_ERROR': {
              updateApprovalAssistant({ status: 'error', error: String(evt.message || 'Error') });
              break;
            }
          }
        },
        controller.signal,
      );

      finalizeApprovalAssistant('complete');
    } catch (err) {
      if ((err as Error | undefined)?.name === 'AbortError') {
        finalizeApprovalAssistant('complete');
      } else {
        finalizeApprovalAssistant('error', err instanceof Error ? err.message : 'Approval failed');
      }
    } finally {
      setIsStreaming(false);
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
    }
  }, [scopeId, selectedService, services, messages, activeConvId, saveCurrentConversation]);

  const handleStop = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  // Auto-start onboarding when selected and chat is empty.
  useEffect(() => {
    if (selectedService !== 'onboarding' || messages.length > 0) {
      if (selectedService !== 'onboarding') setOnboardingState(null);
      return;
    }
    if (onboardingState) return;
    // Show provider selection immediately.
    setOnboardingState({ step: 'select_provider' });
    setMessages([{
      id: `onboarding-${Date.now()}`,
      role: 'assistant',
      content: ONBOARDING_PROVIDER_PROMPT,
      timestamp: Date.now(),
      status: 'complete',
    }]);
  }, [selectedService, messages.length, onboardingState]);

  const handleNewChat = useCallback(() => {
    setMessages([]);
    setDebugEvents([]);
    setActiveConvId(null);
    streamingProxyRoomRef.current = null;
    setComposerText('');
    setConversationRoute(undefined);
    setConversationModel(undefined);
    setOnboardingState(null);
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
      setConversationRoute(conv?.llmRoute);
      setConversationModel(trimOptional(conv?.llmModel));
      setDebugEvents([]);
      setComposerText('');
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
        streamingProxyRoomRef.current = null;
        setConversationRoute(undefined);
        setConversationModel(undefined);
        setDebugEvents([]);
      }
    } catch { /* ignore */ }
  }, [scopeId, activeConvId]);

  // Endpoint tabs
  type EndpointTab = 'chat' | 'query' | 'invoke' | 'raw';
  const [activeEndpoint, setActiveEndpoint] = useState<EndpointTab>('chat');
  const endpointTabs: { id: EndpointTab; label: string }[] = [
    { id: 'chat', label: 'Chat' },
    { id: 'query', label: 'Query' },
    { id: 'invoke', label: 'Invoke' },
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

  // ── Invoke tab: user-facing streaming service invoke ──
  type InvokeDraft = {
    endpointId: string;
    prompt: string;
    actorId: string;
    headersText: string;
    headers: Record<string, string>;
  };
  const invokeSupport = useMemo(
    () => getInvokeSurfaceSupport(activeService),
    [activeService],
  );
  const invokeableEndpoints = useMemo(
    () => getStreamableInvokeEndpoints(activeService),
    [activeService],
  );
  const hiddenInvokeEndpoints = useMemo(
    () => getNonStreamableInvokeEndpoints(activeService),
    [activeService],
  );
  const [invokeEndpointId, setInvokeEndpointId] = useState('chat');
  useEffect(() => {
    if (invokeableEndpoints.length > 0 && !invokeableEndpoints.some(ep => ep.endpointId === invokeEndpointId)) {
      setInvokeEndpointId(invokeableEndpoints[0].endpointId);
    }
  }, [invokeEndpointId, invokeableEndpoints]);
  const activeInvokeEndpoint = invokeableEndpoints.find(endpoint => endpoint.endpointId === invokeEndpointId) || invokeableEndpoints[0] || null;
  const [invokePrompt, setInvokePrompt] = useState('');
  const [invokeActorId, setInvokeActorId] = useState('');
  const [invokeHeadersText, setInvokeHeadersText] = useState('');
  const [invokeAdvancedOpen, setInvokeAdvancedOpen] = useState(false);
  const [invokeEvents, setInvokeEvents] = useState<Array<{ type: string; data: any }>>([]);
  const [invokeHistory, setInvokeHistory] = useState<InvokeHistoryEntry[]>([]);
  const [activeInvokeHistoryId, setActiveInvokeHistoryId] = useState<string | null>(null);
  const [invokeLoading, setInvokeLoading] = useState(false);
  const [invokeFormError, setInvokeFormError] = useState<string | null>(null);
  const [invokeResumeLoading, setInvokeResumeLoading] = useState(false);
  const [invokeResumeError, setInvokeResumeError] = useState<string | null>(null);
  const invokeAbortRef = useRef<AbortController | null>(null);
  const invokeHeaderParse = useMemo(
    () => parseInvokeHeaders(invokeHeadersText),
    [invokeHeadersText],
  );
  const invokeHeaderError = invokeHeaderParse.errors[0] || null;
  const invokeRequestPayload = useMemo(
    () => buildInvokeRequestPayload(invokePrompt, invokeActorId, invokeHeaderParse.headers),
    [invokeActorId, invokeHeaderParse.headers, invokePrompt],
  );
  const invokeRequestPreview = useMemo(
    () => JSON.stringify(invokeRequestPayload, null, 2),
    [invokeRequestPayload],
  );
  const invokeTransportLabel = useMemo(
    () => buildInvokeTransportLabel(scopeId, activeService, activeInvokeEndpoint?.endpointId || invokeEndpointId),
    [activeInvokeEndpoint?.endpointId, activeService, invokeEndpointId, scopeId],
  );
  const invokeSummary = useMemo(
    () => summarizeInvokeEvents(invokeEvents),
    [invokeEvents],
  );
  const invokeHistoryForService = useMemo(
    () => [...invokeHistory]
      .filter(entry => entry.serviceId === activeService.id)
      .sort((left, right) => right.updatedAt - left.updatedAt),
    [activeService.id, invokeHistory],
  );
  const activeInvokeHistoryEntry = useMemo(
    () => invokeHistoryForService.find(entry => entry.id === activeInvokeHistoryId) || invokeHistoryForService[0] || null,
    [activeInvokeHistoryId, invokeHistoryForService],
  );
  const pendingInvokeHumanInput = useMemo(
    () => extractInvokePendingHumanInput(
      invokeEvents,
      activeService.id,
      trimOptional(invokeActorId)
        || trimOptional(activeInvokeHistoryEntry?.request.actorId)
        || trimOptional(invokeSummary.actorId)
        || trimOptional(activeInvokeHistoryEntry?.summary.actorId),
    ),
    [activeInvokeHistoryEntry?.request.actorId, activeInvokeHistoryEntry?.summary.actorId, activeService.id, invokeActorId, invokeEvents, invokeSummary.actorId],
  );

  useEffect(() => {
    setInvokeFormError(null);
  }, [activeService?.id, invokeEndpointId, invokeHeadersText, invokePrompt]);

  useEffect(() => {
    if (!pendingInvokeHumanInput) {
      setInvokeResumeLoading(false);
      setInvokeResumeError(null);
    }
  }, [pendingInvokeHumanInput]);

  useEffect(() => {
    setInvokeEvents([]);
    setInvokeActorId('');
    setInvokeHeadersText('');
    setInvokeAdvancedOpen(false);
    setActiveInvokeHistoryId(null);
    setInvokeResumeLoading(false);
    setInvokeResumeError(null);
  }, [activeService?.id]);

  useEffect(() => {
    if (activeInvokeHistoryId && invokeHistoryForService.some(entry => entry.id === activeInvokeHistoryId)) {
      return;
    }

    if (invokeLoading) {
      return;
    }

    if (invokeHistoryForService.length === 0) {
      setActiveInvokeHistoryId(null);
      return;
    }

    const latest = invokeHistoryForService[0];
    setActiveInvokeHistoryId(latest.id);
    setInvokeEndpointId(latest.request.endpointId);
    setInvokePrompt(latest.request.prompt);
    setInvokeActorId(latest.request.actorId);
    setInvokeHeadersText(latest.request.headersText);
    setInvokeAdvancedOpen(Boolean(latest.request.actorId || latest.request.headersText.trim()));
    setInvokeEvents(latest.events);
  }, [activeInvokeHistoryId, invokeHistoryForService, invokeLoading]);

  const syncInvokeHistoryEntry = useCallback((entryId: string, events: Array<{ type: string; data: any }>) => {
    const summary = summarizeInvokeEvents(events);
    setInvokeHistory(prev => prev.map(entry => (
      entry.id === entryId
        ? { ...entry, events, summary, updatedAt: Date.now() }
        : entry
    )));
  }, []);

  const runInvokeDraft = useCallback(async (draft: InvokeDraft) => {
    if (!scopeId || !invokeSupport.supported) {
      return;
    }

    const prompt = draft.prompt.trim();
    if (!prompt) {
      setInvokeFormError('Prompt is required.');
      return;
    }

    const headerParse = parseInvokeHeaders(draft.headersText);
    if (headerParse.errors[0]) {
      setInvokeFormError(headerParse.errors[0]);
      return;
    }

    const normalizedActorId = trimOptional(draft.actorId) || '';
    const sessionId = genId();
    const createdAt = Date.now();
    const sessionRecord: InvokeHistoryEntry = {
      id: sessionId,
      serviceId: activeService.id,
      createdAt,
      updatedAt: createdAt,
      request: {
        prompt,
        actorId: normalizedActorId,
        headersText: draft.headersText,
        endpointId: draft.endpointId,
        endpointLabel: invokeableEndpoints.find(endpoint => endpoint.endpointId === draft.endpointId)?.displayName || draft.endpointId,
      },
      transportLabel: buildInvokeTransportLabel(scopeId, activeService, draft.endpointId),
      requestPreview: JSON.stringify(buildInvokeRequestPayload(prompt, normalizedActorId, headerParse.headers), null, 2),
      events: [],
      summary: summarizeInvokeEvents([]),
    };

    setInvokeFormError(null);
    setInvokeLoading(true);
    setInvokeResumeLoading(false);
    setInvokeResumeError(null);
    setInvokeEvents([]);
    setActiveInvokeHistoryId(sessionId);
    setInvokeHistory(prev => [sessionRecord, ...prev]);
    const controller = new AbortController();
    invokeAbortRef.current = controller;
    const collected: Array<{ type: string; data: any }> = [];

    try {
      const serviceId = activeService.kind === 'nyxid-chat' ? NYXID_CHAT_SERVICE_ID : activeService.id;

      if (activeService.kind === 'nyxid-chat' && !nyxidChatBoundRef.current) {
        await api.scope.bindGAgent(scopeId, 'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent', 'NyxID Chat', NYXID_CHAT_SERVICE_ID);
        nyxidChatBoundRef.current = true;
      }

      const pushFrame = (frame: any) => {
        const evt = normalizeBackendSseFrame(frame);
        if (!evt) {
          return;
        }

        collected.push({ type: evt.type, data: evt });
        const nextEvents = [...collected];
        setInvokeEvents(nextEvents);
        setActiveInvokeHistoryId(sessionId);
        syncInvokeHistoryEntry(sessionId, nextEvents);
      };

      if (activeService.kind === 'streaming-proxy') {
        const roomId = await ensureStreamingProxyRoom();
        await api.streamingProxy.streamChat(
          scopeId,
          roomId,
          prompt,
          pushFrame,
          controller.signal,
          activeConvId || undefined,
          conversationRoute,
          conversationModel,
        );
      } else {
        await api.scope.streamInvoke(
          scopeId,
          serviceId,
          prompt,
          pushFrame,
          controller.signal,
          draft.endpointId,
          headerParse.headers,
          normalizedActorId || undefined,
        );
      }

      if (collected.length === 0) {
        const nextEvents = [{ type: 'INFO', data: { message: 'No events received.' } }];
        setInvokeEvents(nextEvents);
        syncInvokeHistoryEntry(sessionId, nextEvents);
      } else {
        syncInvokeHistoryEntry(sessionId, [...collected]);
      }
    } catch (e: any) {
      const nextEvents = [
        ...collected,
        e?.name === 'AbortError'
          ? { type: 'RUN_STOPPED', data: { reason: 'Invocation stopped by user.' } }
          : { type: 'ERROR', data: { message: e?.message || JSON.stringify(e) } },
      ];
      setInvokeEvents(nextEvents);
      syncInvokeHistoryEntry(sessionId, nextEvents);
    } finally {
      setInvokeLoading(false);
      invokeAbortRef.current = null;
    }
  }, [
    activeConvId,
    activeService,
    conversationModel,
    conversationRoute,
    ensureStreamingProxyRoom,
    invokeSupport.supported,
    invokeableEndpoints,
    nyxidChatBoundRef,
    scopeId,
    syncInvokeHistoryEntry,
  ]);

  const handleInvokeSubmit = async () => {
    await runInvokeDraft({
      endpointId: activeInvokeEndpoint?.endpointId || invokeEndpointId,
      prompt: invokePrompt,
      actorId: invokeActorId,
      headersText: invokeHeadersText,
      headers: invokeHeaderParse.headers,
    });
  };

  const handleInvokeStop = () => { invokeAbortRef.current?.abort(); };

  const handleLoadInvokeFixture = useCallback(() => {
    if (activeService.kind === 'streaming-proxy') {
      setInvokePrompt('Summarize the strongest and weakest arguments from this room discussion and tell me where the team still disagrees.');
      return;
    }

    if (activeService.kind === 'nyxid-chat') {
      setInvokePrompt('Help me verify that this scope chat binding works. Explain your answer briefly and mention any tool usage you perform.');
      return;
    }

    setInvokePrompt('Review this request and explain your final answer: refund for order #92817 — third time asking.');
  }, [activeService.kind]);

  const handleReplayLastInvoke = useCallback(async () => {
    const latest = invokeHistoryForService[0];
    if (!latest || invokeLoading) {
      return;
    }

    setInvokeEndpointId(latest.request.endpointId);
    setInvokePrompt(latest.request.prompt);
    setInvokeActorId(latest.request.actorId);
    setInvokeHeadersText(latest.request.headersText);
    setInvokeAdvancedOpen(Boolean(latest.request.actorId || latest.request.headersText.trim()));

    await runInvokeDraft({
      endpointId: latest.request.endpointId,
      prompt: latest.request.prompt,
      actorId: latest.request.actorId,
      headersText: latest.request.headersText,
      headers: parseInvokeHeaders(latest.request.headersText).headers,
    });
  }, [invokeHistoryForService, invokeLoading, runInvokeDraft]);

  const handleSelectInvokeHistory = useCallback((historyId: string) => {
    const record = invokeHistoryForService.find(entry => entry.id === historyId);
    if (!record) {
      return;
    }

    setActiveInvokeHistoryId(historyId);
    setInvokeEndpointId(record.request.endpointId);
    setInvokePrompt(record.request.prompt);
    setInvokeActorId(record.request.actorId);
    setInvokeHeadersText(record.request.headersText);
    setInvokeAdvancedOpen(Boolean(record.request.actorId || record.request.headersText.trim()));
    setInvokeResumeLoading(false);
    setInvokeResumeError(null);
    setInvokeEvents(record.events);
  }, [invokeHistoryForService]);

  const handleInvokeResumeHumanInput = useCallback(async (userInput: string) => {
    if (!scopeId || invokeLoading || invokeResumeLoading) {
      return;
    }

    const pending = pendingInvokeHumanInput;
    const selectedHistory = activeInvokeHistoryEntry;
    const normalizedInput = String(userInput || '').trim();

    if (!pending || !selectedHistory) {
      return;
    }

    if (!normalizedInput) {
      setInvokeResumeError('A response is required before the run can continue.');
      return;
    }

    setInvokeResumeError(null);
    setInvokeResumeLoading(true);

    try {
      const response = await api.scope.resumeRun(
        scopeId,
        pending.serviceId || activeService.id,
        pending.runId,
        {
          stepId: pending.stepId,
          userInput: normalizedInput,
          approved: true,
          actorId: pending.actorId,
        },
      );

      const timestamp = Date.now();
      const nextEvents = [
        ...invokeEvents,
        {
          type: 'HUMAN_INPUT_RESPONSE',
          data: {
            timestamp,
            stepId: pending.stepId,
            runId: pending.runId,
            approved: true,
            userInput: normalizedInput,
            actorId: response?.actorId || pending.actorId || selectedHistory.summary.actorId || undefined,
            commandId: response?.commandId || '',
            correlationId: response?.correlationId || '',
          },
        },
      ];

      setInvokeEvents(nextEvents);
      setActiveInvokeHistoryId(selectedHistory.id);
      syncInvokeHistoryEntry(selectedHistory.id, nextEvents);
    } catch (error: any) {
      setInvokeResumeError(error?.message || 'Resume failed.');
    } finally {
      setInvokeResumeLoading(false);
    }
  }, [
    activeInvokeHistoryEntry,
    activeService.id,
    invokeEvents,
    invokeLoading,
    invokeResumeLoading,
    pendingInvokeHumanInput,
    scopeId,
    syncInvokeHistoryEntry,
  ]);

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
    <div className="scope-page flex flex-col h-full">
      {/* Header */}
      <header className="workspace-page-header flex-shrink-0 border-b border-[#E6E3DE] bg-white/95 backdrop-blur-sm px-5">
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
                showDebug ? '' : 'border-[#E6E3DE] text-gray-400 hover:bg-[#F7F5F2]'
              }`}
              style={showDebug ? {
                borderColor: 'rgba(var(--accent-rgb), 0.28)',
                background: 'var(--accent-soft-end)',
                color: 'var(--accent-text)',
              } : undefined}
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
      ) : activeEndpoint === 'invoke' ? (
        <InvokeWorkbench
          scopeId={scopeId}
          service={activeService}
          invokeSupport={invokeSupport}
          invokeableEndpoints={invokeableEndpoints}
          hiddenInvokeEndpoints={hiddenInvokeEndpoints}
          activeEndpoint={activeInvokeEndpoint}
          endpointId={activeInvokeEndpoint?.endpointId || invokeEndpointId}
          onEndpointChange={setInvokeEndpointId}
          prompt={invokePrompt}
          onPromptChange={setInvokePrompt}
          actorId={invokeActorId}
          onActorIdChange={setInvokeActorId}
          headersText={invokeHeadersText}
          onHeadersTextChange={setInvokeHeadersText}
          advancedOpen={invokeAdvancedOpen}
          onAdvancedOpenChange={setInvokeAdvancedOpen}
          formError={invokeFormError}
          headerError={invokeHeaderError}
          transportLabel={invokeTransportLabel}
          requestPreview={invokeRequestPreview}
          events={invokeEvents}
          summary={invokeSummary}
          loading={invokeLoading}
          pendingHumanInput={pendingInvokeHumanInput}
          resumeLoading={invokeResumeLoading}
          resumeError={invokeResumeError}
          history={invokeHistoryForService}
          activeHistoryId={activeInvokeHistoryId}
          onSelectHistory={handleSelectInvokeHistory}
          onInvoke={() => { void handleInvokeSubmit(); }}
          onStop={handleInvokeStop}
          onLoadFixture={handleLoadInvokeFixture}
          onSaveRequest={() => { void copyTextToClipboard(invokeRequestPreview); }}
          onReplayLast={() => { void handleReplayLastInvoke(); }}
          onResumeHumanInput={value => { void handleInvokeResumeHumanInput(value); }}
          onGoToChat={() => setActiveEndpoint('chat')}
          onGoToRaw={() => setActiveEndpoint('raw')}
          renderResponse={text => renderContent(text, 'assistant')}
          copyText={copyTextToClipboard}
        />
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

      {/* Chat Area — virtualized with Pretext height estimation */}
      <div
        ref={scrollParentRef}
        className="flex-1 min-h-0 overflow-auto bg-[#FAFAF8]"
        onScroll={handleChatScroll}
      >
        {messages.length === 0 ? (
          <div className="max-w-3xl mx-auto py-6 px-4">
            <div className="flex flex-col items-center justify-center py-20 text-center">
              <div
                className="w-12 h-12 rounded-2xl flex items-center justify-center mb-4 shadow-lg"
                style={{
                  background: 'linear-gradient(135deg, var(--accent-gradient-start) 0%, var(--accent-gradient-end) 100%)',
                  boxShadow: '0 16px 32px var(--accent-shadow-strong)',
                }}
              >
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
          </div>
        ) : (
          <div
            style={{ height: chatVirtualizer.getTotalSize() + 48, position: 'relative' }}
          >
            <div className="max-w-3xl mx-auto px-4">
              {chatVirtualizer.getVirtualItems().map((virtualRow) => {
                const msg = messages[virtualRow.index];
                const index = virtualRow.index;
                return (
                  <div
                    key={msg.id}
                    data-index={virtualRow.index}
                    ref={chatVirtualizer.measureElement}
                    style={{
                      position: 'absolute',
                      top: 0,
                      left: 0,
                      width: '100%',
                      transform: `translateY(${virtualRow.start + 24}px)`,
                    }}
                  >
                    <div className="pb-5 max-w-3xl mx-auto px-4">
                      <ChatBubble
                        msg={msg}
                        canRegenerate={!isStreaming && (msg.role === 'user' || findPreviousUserMessageIndex(messages, index) >= 0)}
                        canEdit={msg.role === 'user'}
                        onCopy={handleCopyMessage}
                        onCopyMarkdown={handleCopyMessageMarkdown}
                        onCopyPlainText={handleCopyMessagePlainText}
                        onRegenerate={() => handleRegenerate(index)}
                        onEdit={() => handleEditMessage(index)}
                        onDelete={() => { void handleDeleteMessage(index); }}
                        onApprove={handleToolApproval}
                        onResumeHumanInput={handleResumeHumanInput}
                      />
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
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
            value={composerText}
            onChange={setComposerText}
            onSend={handleSend}
            onStop={handleStop}
            isStreaming={isStreaming}
            disabled={!scopeId}
            focusToken={composerFocusToken}
            pendingAttachments={pendingAttachments}
            onAttach={handleAttach}
            onRemoveAttachment={handleRemoveAttachment}
            footer={(
              <ConversationLlmConfigBar
                mode={activeService.kind === 'streaming-proxy' ? 'streaming-proxy' : 'conversation'}
                routeValue={conversationRoute}
                routeOptions={routeOptions}
                modelValue={conversationModel}
                modelGroups={modelGroups}
                effectiveRoute={effectiveRoute}
                effectiveRouteLabel={effectiveRouteLabel}
                effectiveModel={effectiveModel}
                modelsLoading={modelsLoading}
                disabled={isStreaming || !scopeId}
                onRouteChange={handleConversationRouteChange}
                onModelChange={handleConversationModelChange}
                onReset={handleResetConversationLlm}
              />
            )}
          />
          <div className="mt-1.5 text-center text-[11px] text-gray-300">
            Service: {activeService.kind === 'nyxid-chat' ? 'nyxid-chat' : activeService.id}
            {activeService.kind === 'streaming-proxy' ? ' · Room route: ' : ' · Route: '}{effectiveRouteLabel}
            {activeService.kind === 'streaming-proxy' ? ' · Room model: ' : ' · Model: '}{effectiveModel || 'provider default'}
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
