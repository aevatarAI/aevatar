/**
 * Pretext-powered height estimation for chat messages.
 *
 * Uses Canvas-based text measurement (via @chenglou/pretext) to predict
 * message bubble heights without DOM reflow. Used as `estimateSize` for
 * @tanstack/react-virtual — the virtualizer corrects estimates via
 * `measureElement` after render.
 */

import { useCallback, useRef } from 'react';
import { prepare, layout } from '@chenglou/pretext';
import { parseMarkdownBlocks, sanitizeAssistantMessageContent } from './chatContent';

/* ─── Font descriptors matching the actual CSS ─── */

const BODY_FONT = '14px Inter, system-ui, sans-serif';
const MONO_FONT = '13px "JetBrains Mono", monospace';

// Line heights from the actual CSS classes
const ASSISTANT_LINE_HEIGHT = 29; // 14px * 2.05 (leading-[2.05])
const USER_LINE_HEIGHT = 23;      // 14px * 1.625 (leading-relaxed)
const CODE_LINE_HEIGHT = 20;      // leading-5
/* ─── Pretext handle cache (module-level singleton) ─── */

type PreparedHandle = ReturnType<typeof prepare>;

const handleCache = new Map<string, PreparedHandle>();

function getPreparedHandle(text: string, font: string): PreparedHandle {
  // Pretext prepare() is content+font dependent, so we cache per unique combo.
  // For estimation, we batch all text for a font together per message.
  const key = `${font}::${text.length}::${text.slice(0, 64)}`;
  const cached = handleCache.get(key);
  if (cached) return cached;

  // Limit cache size to avoid memory leaks
  if (handleCache.size > 2000) {
    const firstKey = handleCache.keys().next().value;
    if (firstKey) handleCache.delete(firstKey);
  }

  const handle = prepare(text, font);
  handleCache.set(key, handle);
  return handle;
}

/* ─── Fixed height constants for non-text elements ─── */

const BUBBLE_PADDING_Y = 24;        // py-3 = 12px * 2
const BUBBLE_HEADER_HEIGHT = 24;     // role label + timestamp row
const GAP_BETWEEN_MESSAGES = 20;     // space-y-5
const THINKING_COLLAPSED_HEIGHT = 32; // button row height
const STEPS_COLLAPSED_HEIGHT = 32;    // button row height

const ATTACHMENT_HEIGHT = 48;         // per attachment badge
const IMAGE_MEDIA_HEIGHT = 320;       // max-h-[300px] + margins
const AUDIO_MEDIA_HEIGHT = 56;        // audio controls + margin
const VIDEO_MEDIA_HEIGHT = 320;       // max-h-[300px] + margins
const ERROR_BLOCK_HEIGHT = 56;        // error box
const PENDING_APPROVAL_HEIGHT = 120;  // approval box
const PENDING_HUMAN_INPUT_BASE = 80;  // base height
const PENDING_HUMAN_INPUT_CHOICE = 40; // per choice button

const CODE_BLOCK_HEADER_HEIGHT = 28;  // language label bar
const CODE_BLOCK_PADDING_Y = 16;     // py-2 * 2

/* ─── Height estimation for a text block ─── */

function estimateTextHeight(text: string, font: string, widthPx: number, lineHeight: number): number {
  if (!text || widthPx <= 0) return 0;
  try {
    const handle = getPreparedHandle(text, font);
    const result = layout(handle, widthPx, lineHeight);
    return result.height;
  } catch {
    // Fallback: rough line count estimation
    const avgCharsPerLine = Math.max(1, Math.floor(widthPx / 8));
    const lines = text.split('\n').reduce((total, line) => {
      return total + Math.max(1, Math.ceil(line.length / avgCharsPerLine));
    }, 0);
    return lines * lineHeight;
  }
}

/* ─── Message-level estimation ─── */

export type MessageEstimationInput = {
  id: string;
  content: string;
  role: string;
  thinking?: string;
  attachments?: { mediaType: string }[];
  mediaParts?: { type: string }[];
  steps?: unknown[];
  toolCalls?: unknown[];
  error?: string;
  status?: string;
  pendingApproval?: unknown;
  pendingHumanInput?: { options?: string[] };
};

export function estimateMessageHeight(
  msg: MessageEstimationInput,
  containerWidthPx: number,
): number {
  const isUser = msg.role === 'user';

  // Effective content width inside the bubble
  // max-w-[82%] for user, max-w-[88%] for assistant, minus px-4 (32px)
  const bubbleMaxWidth = containerWidthPx * (isUser ? 0.82 : 0.88);
  const contentWidth = Math.max(100, bubbleMaxWidth - 32);

  let totalHeight = BUBBLE_PADDING_Y + BUBBLE_HEADER_HEIGHT;

  // ── Main content (markdown) ──
  const displayContent = isUser ? msg.content : sanitizeAssistantMessageContent(msg.content);
  if (displayContent) {
    const blocks = parseMarkdownBlocks(displayContent);
    for (const block of blocks) {
      switch (block.kind) {
        case 'code': {
          const codeHeight = estimateTextHeight(block.code, MONO_FONT, contentWidth - 24, CODE_LINE_HEIGHT);
          totalHeight += codeHeight + CODE_BLOCK_PADDING_Y + (block.lang ? CODE_BLOCK_HEADER_HEIGHT : 0) + 8; // my-2
          break;
        }
        case 'paragraph':
          totalHeight += estimateTextHeight(
            block.lines.join('\n'),
            BODY_FONT,
            contentWidth,
            isUser ? USER_LINE_HEIGHT : ASSISTANT_LINE_HEIGHT,
          ) + 4; // my-1
          break;
        case 'heading':
          totalHeight += estimateTextHeight(
            block.text,
            BODY_FONT,
            contentWidth,
            isUser ? USER_LINE_HEIGHT : ASSISTANT_LINE_HEIGHT,
          ) + 16; // mt-3 + mb-1
          break;
        case 'blockquote':
          totalHeight += estimateTextHeight(
            block.lines.join('\n'),
            BODY_FONT,
            contentWidth - 12, // pl-3 border
            isUser ? USER_LINE_HEIGHT : ASSISTANT_LINE_HEIGHT,
          ) + 8;
          break;
        case 'unordered-list':
        case 'ordered-list':
          for (const item of block.items) {
            totalHeight += estimateTextHeight(
              item,
              BODY_FONT,
              contentWidth - 20, // pl-5
              isUser ? USER_LINE_HEIGHT : ASSISTANT_LINE_HEIGHT,
            ) + 4; // space-y-1
          }
          totalHeight += 8; // my-2
          break;
        case 'thematic-break':
          totalHeight += 24; // my-3 * 2
          break;
      }
    }
  }

  // ── Thinking block ──
  if (!isUser && msg.thinking) {
    // Default: expanded for assistant
    totalHeight += THINKING_COLLAPSED_HEIGHT;
  }

  // ── Steps / Tool calls ──
  const stepCount = (msg.steps?.length ?? 0) + (msg.toolCalls?.length ?? 0);
  if (stepCount > 0) {
    totalHeight += STEPS_COLLAPSED_HEIGHT; // collapsed by default
  }

  // ── Attachments ──
  if (msg.attachments?.length) {
    for (const att of msg.attachments) {
      if (att.mediaType.startsWith('image/')) totalHeight += IMAGE_MEDIA_HEIGHT;
      else if (att.mediaType.startsWith('audio/')) totalHeight += AUDIO_MEDIA_HEIGHT;
      else if (att.mediaType.startsWith('video/')) totalHeight += VIDEO_MEDIA_HEIGHT;
      else totalHeight += ATTACHMENT_HEIGHT;
    }
  }

  // ── Media parts ──
  if (msg.mediaParts?.length) {
    for (const part of msg.mediaParts) {
      if (part.type === 'image') totalHeight += IMAGE_MEDIA_HEIGHT;
      else if (part.type === 'audio') totalHeight += AUDIO_MEDIA_HEIGHT;
      else if (part.type === 'video') totalHeight += VIDEO_MEDIA_HEIGHT;
    }
  }

  // ── Error ──
  if (msg.error) totalHeight += ERROR_BLOCK_HEIGHT;

  // ── Pending approval ──
  if (msg.pendingApproval) totalHeight += PENDING_APPROVAL_HEIGHT;

  // ── Pending human input ──
  if (msg.pendingHumanInput) {
    totalHeight += PENDING_HUMAN_INPUT_BASE +
      (msg.pendingHumanInput.options?.length ?? 0) * PENDING_HUMAN_INPUT_CHOICE;
  }

  // ── Add gap ──
  totalHeight += GAP_BETWEEN_MESSAGES;

  return Math.max(totalHeight, 60); // minimum height
}

/* ─── Textarea height estimation ─── */

export function estimateTextareaHeight(
  text: string,
  widthPx: number,
  minHeight = 82,
  maxHeight = 160,
): number {
  if (!text) return minHeight;
  const lineHeight = 21; // 14px * 1.5
  const height = estimateTextHeight(text, BODY_FONT, widthPx, lineHeight);
  // Add padding: pt-4 (16px) + pb-3 (12px) = 28px
  return Math.max(minHeight, Math.min(height + 28, maxHeight));
}

/* ─── React hook for cached estimation ─── */

export function usePretextEstimator() {
  const cacheRef = useRef(new Map<string, number>());

  const estimate = useCallback((msg: MessageEstimationInput, containerWidth: number): number => {
    // Cache key includes content length for streaming invalidation
    const cacheKey = `${msg.id}:${msg.content.length}:${containerWidth}:${msg.status}`;
    const cached = cacheRef.current.get(cacheKey);
    if (cached !== undefined) return cached;

    // Evict old entries
    if (cacheRef.current.size > 1000) {
      const entries = [...cacheRef.current.entries()];
      for (let i = 0; i < 500; i++) {
        cacheRef.current.delete(entries[i][0]);
      }
    }

    const height = estimateMessageHeight(msg, containerWidth);
    cacheRef.current.set(cacheKey, height);
    return height;
  }, []);

  return estimate;
}
