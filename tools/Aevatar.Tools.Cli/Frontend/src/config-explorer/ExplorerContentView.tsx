import { useMemo, useState } from 'react';
import { parseMarkdownBlocks, sanitizeAssistantMessageContent, tokenizeInlineContent } from '../runtime/chatContent';
import { buildExplorerContentModel, type ExplorerChatMessage, type ExplorerScriptFile } from './contentFormatting';

type Props = {
  fileType: string;
  content: string | null;
};

type RenderTone = 'assistant' | 'user';

export default function ExplorerContentView({ fileType, content }: Props) {
  const model = useMemo(
    () => buildExplorerContentModel(fileType, content ?? ''),
    [content, fileType],
  );

  if (content === null) {
    return (
      <div className="flex min-h-[400px] items-center justify-center px-6 py-12 text-[13px] text-gray-400">
        Could not load file.
      </div>
    );
  }

  if (model.kind === 'chat-history') {
    return <ChatHistoryPreview messages={model.messages} />;
  }

  if (model.kind === 'script-package') {
    return <ScriptPackagePreview scriptPackage={model.package} />;
  }

  return (
    <div className="max-h-[70vh] overflow-y-auto p-4">
      {model.formattedText.trim() ? (
        <pre className="w-full min-h-[400px] rounded-2xl bg-[#FAF8F4] px-4 py-4 text-[13px] font-mono leading-relaxed text-gray-700 whitespace-pre-wrap break-words">
          {model.formattedText}
        </pre>
      ) : (
        <div className="flex min-h-[400px] items-center justify-center rounded-2xl bg-[#FAF8F4] px-6 py-12 text-[13px] text-gray-400">
          Empty file.
        </div>
      )}
    </div>
  );
}

function ChatHistoryPreview({ messages }: { messages: ExplorerChatMessage[] }) {
  return (
    <div className="max-h-[70vh] overflow-y-auto px-4 py-4">
      <div className="mb-4 rounded-2xl border border-[#F0ECE5] bg-[#FAF8F4] px-4 py-3">
        <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Conversation</div>
        <div className="mt-1 text-[14px] font-semibold text-gray-800">
          {messages.length} message{messages.length === 1 ? '' : 's'}
        </div>
      </div>

      {messages.length === 0 ? (
        <div className="flex min-h-[320px] items-center justify-center rounded-2xl bg-[#FAF8F4] px-6 py-12 text-[13px] text-gray-400">
          Empty conversation.
        </div>
      ) : (
        <div className="space-y-4">
          {messages.map((message, index) => (
            <ChatHistoryBubble
              key={message.id || `${message.timestamp}-${index}`}
              message={message}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function ChatHistoryBubble({ message }: { message: ExplorerChatMessage }) {
  const isUser = message.role === 'user';
  const label = isUser ? 'User' : message.role === 'assistant' ? 'Assistant' : message.role;
  const bubbleClass = isUser
    ? 'ml-auto max-w-[82%] rounded-2xl rounded-br-md bg-[#2563EB] px-4 py-3 text-white'
    : 'max-w-[88%] rounded-2xl rounded-bl-md border border-[#F0ECE5] bg-[#FCFBF8] px-4 py-3 text-gray-800';
  const avatarClass = isUser
    ? 'bg-[#DBEAFE] text-[#1D4ED8]'
    : 'bg-[#ECE8FF] text-[#6D28D9]';
  const sanitizedContent = isUser
    ? message.content
    : sanitizeAssistantMessageContent(message.content);

  return (
    <div className={`flex gap-3 ${isUser ? 'justify-end' : 'justify-start'}`}>
      {!isUser && <MessageAvatar label={label} className={avatarClass} />}

      <div className={bubbleClass}>
        <div className={`mb-2 flex items-center gap-2 text-[11px] ${isUser ? 'text-white/75' : 'text-gray-400'}`}>
          <span className="font-semibold uppercase tracking-[0.12em]">{label}</span>
          <span>{formatTimestamp(message.timestamp)}</span>
          {message.status && message.status !== 'complete' && (
            <span className={`rounded-full px-2 py-0.5 ${isUser ? 'bg-white/10 text-white/85' : 'bg-[#F4F0EA] text-gray-500'}`}>
              {message.status}
            </span>
          )}
        </div>

        {!isUser && message.thinking && (
          <ChatThinkingBlock text={message.thinking} />
        )}

        <div className="break-words text-[14px] leading-relaxed">
          {renderMarkdownContent(sanitizedContent, isUser ? 'user' : 'assistant')}
        </div>

        {message.error && (
          <div className={`mt-3 rounded-xl border px-3 py-2 text-[12px] ${
            isUser
              ? 'border-white/15 bg-white/10 text-white/90'
              : 'border-red-200 bg-red-50 text-red-600'
          }`}>
            {message.error}
          </div>
        )}
      </div>

      {isUser && <MessageAvatar label={label} className={avatarClass} />}
    </div>
  );
}

function ChatThinkingBlock({ text }: { text: string }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="mb-3">
      <button
        onClick={() => setOpen(value => !value)}
        className="flex items-center gap-2 text-[12px] text-gray-400 hover:text-gray-600"
      >
        <span className={`inline-block h-2 w-2 rounded-full bg-violet-400 transition-transform ${open ? 'scale-110' : ''}`} />
        <span>Thinking</span>
      </button>
      {open && (
        <div className="mt-2 rounded-xl bg-[#F6F2FF] px-3 py-2 text-[12px] italic leading-relaxed text-gray-500 whitespace-pre-wrap">
          {text}
        </div>
      )}
    </div>
  );
}

function ScriptPackagePreview({ scriptPackage }: { scriptPackage: { format: string; entryBehaviorTypeName: string; entrySourcePath: string; csharpSources: ExplorerScriptFile[]; protoFiles: ExplorerScriptFile[] } }) {
  const files = [
    ...scriptPackage.csharpSources.map(file => ({ ...file, label: 'C#' })),
    ...scriptPackage.protoFiles.map(file => ({ ...file, label: 'Proto' })),
  ];

  return (
    <div className="max-h-[70vh] overflow-y-auto px-4 py-4">
      <div className="mb-4 rounded-2xl border border-[#F0ECE5] bg-[#FAF8F4] px-4 py-3">
        <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Script Package</div>
        <div className="mt-2 flex flex-wrap gap-2 text-[12px] text-gray-600">
          <PackageChip label={`${scriptPackage.csharpSources.length} C# file${scriptPackage.csharpSources.length === 1 ? '' : 's'}`} />
          <PackageChip label={`${scriptPackage.protoFiles.length} proto file${scriptPackage.protoFiles.length === 1 ? '' : 's'}`} />
          {scriptPackage.entrySourcePath && <PackageChip label={`Entry: ${scriptPackage.entrySourcePath}`} />}
          {scriptPackage.entryBehaviorTypeName && <PackageChip label={`Behavior: ${scriptPackage.entryBehaviorTypeName}`} />}
        </div>
      </div>

      {files.length === 0 ? (
        <div className="flex min-h-[320px] items-center justify-center rounded-2xl bg-[#FAF8F4] px-6 py-12 text-[13px] text-gray-400">
          No files in package.
        </div>
      ) : (
        <div className="space-y-4">
          {files.map((file, index) => (
            <div key={`${file.path}-${index}`} className="overflow-hidden rounded-2xl border border-[#EEEAE4] bg-white">
              <div className="flex items-center justify-between gap-3 border-b border-[#F2EEE8] bg-[#FAF8F4] px-4 py-3">
                <div className="min-w-0">
                  <div className="text-[13px] font-semibold text-gray-800">{file.path}</div>
                  <div className="text-[11px] uppercase tracking-[0.12em] text-gray-400">{file.label}</div>
                </div>
                {file.path === scriptPackage.entrySourcePath && (
                  <span className="rounded-full bg-[#EBF0FF] px-2.5 py-1 text-[11px] font-semibold text-[#3B5CCC]">
                    Entry
                  </span>
                )}
              </div>
              <pre className="overflow-x-auto px-4 py-4 text-[13px] font-mono leading-relaxed text-gray-700 whitespace-pre-wrap break-words">
                {file.content}
              </pre>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function PackageChip({ label }: { label: string }) {
  return (
    <span className="rounded-full border border-[#E7E1D9] bg-white px-2.5 py-1 text-[12px] text-gray-600">
      {label}
    </span>
  );
}

function MessageAvatar({ label, className }: { label: string; className: string }) {
  return (
    <div className={`mt-1 flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full text-[11px] font-semibold ${className}`}>
      {(label[0] || '?').toUpperCase()}
    </div>
  );
}

function formatTimestamp(timestamp: number): string {
  if (!Number.isFinite(timestamp) || timestamp <= 0) {
    return '';
  }

  return new Date(timestamp).toLocaleString();
}

function renderMarkdownContent(text: string, tone: RenderTone = 'assistant') {
  if (!text) {
    return null;
  }

  return parseMarkdownBlocks(text).map((block, index) => renderBlock(block, tone, index));
}

function renderBlock(
  block: ReturnType<typeof parseMarkdownBlocks>[number],
  tone: RenderTone,
  key: number,
) {
  switch (block.kind) {
    case 'code': {
      const containerClass = tone === 'user'
        ? 'my-2 overflow-hidden rounded-xl border border-white/15'
        : 'my-2 overflow-hidden rounded-xl border border-[#EAE5DE]';
      const headerClass = tone === 'user'
        ? 'border-b border-white/10 bg-white/10 px-3 py-1 text-[11px] font-mono text-white/75'
        : 'border-b border-[#F1EAE2] bg-[#FAF8F4] px-3 py-1 text-[11px] font-mono text-gray-500';
      const preClass = tone === 'user'
        ? 'bg-white/5 px-3 py-2 text-[13px] font-mono leading-5 text-white whitespace-pre-wrap break-words'
        : 'bg-white px-3 py-2 text-[13px] font-mono leading-5 text-gray-700 whitespace-pre-wrap break-words';
      return (
        <div key={key} className={containerClass}>
          {block.lang && <div className={headerClass}>{block.lang}</div>}
          <pre className={preClass}>{block.code}</pre>
        </div>
      );
    }

    case 'heading': {
      const sizes = ['text-[20px]', 'text-[18px]', 'text-[16px]', 'text-[15px]', 'text-[14px]', 'text-[13px]'];
      const headingClass = tone === 'user'
        ? `${sizes[Math.min(block.level - 1, sizes.length - 1)]} mb-1 mt-3 font-semibold text-white`
        : `${sizes[Math.min(block.level - 1, sizes.length - 1)]} mb-1 mt-3 font-semibold text-gray-900`;
      return (
        <div key={key} className={headingClass}>
          {renderInline(block.text, tone)}
        </div>
      );
    }

    case 'blockquote': {
      const quoteClass = tone === 'user'
        ? 'my-2 border-l-2 border-white/30 pl-3 text-white/90'
        : 'my-2 border-l-2 border-[#D8D1C7] pl-3 text-gray-600';
      return (
        <blockquote key={key} className={quoteClass}>
          {renderLines(block.lines, tone)}
        </blockquote>
      );
    }

    case 'unordered-list': {
      const listClass = tone === 'user'
        ? 'my-2 list-disc space-y-1 pl-5 text-white'
        : 'my-2 list-disc space-y-1 pl-5 text-gray-800';
      return (
        <ul key={key} className={listClass}>
          {block.items.map((item, index) => (
            <li key={index} className="break-words">
              {renderInline(item, tone)}
            </li>
          ))}
        </ul>
      );
    }

    case 'ordered-list': {
      const listClass = tone === 'user'
        ? 'my-2 list-decimal space-y-1 pl-5 text-white'
        : 'my-2 list-decimal space-y-1 pl-5 text-gray-800';
      return (
        <ol key={key} className={listClass}>
          {block.items.map((item, index) => (
            <li key={index} className="break-words">
              {renderInline(item, tone)}
            </li>
          ))}
        </ol>
      );
    }

    case 'thematic-break':
      return <hr key={key} className={tone === 'user' ? 'my-3 border-white/20' : 'my-3 border-[#E5DED3]'} />;

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
  return lines.map((line, index) => (
    <span key={index}>
      {renderInline(line, tone)}
      {index < lines.length - 1 && <br />}
    </span>
  ));
}

function renderInline(text: string, tone: RenderTone) {
  return tokenizeInlineContent(text).map((token, index) => {
    if (token.kind === 'code') {
      const codeClass = tone === 'user'
        ? 'rounded bg-white/15 px-1 py-0.5 font-mono text-[12px] text-white'
        : 'rounded bg-[#F3EFE9] px-1 py-0.5 font-mono text-[12px] text-[#B45309]';
      return (
        <code key={index} className={codeClass}>
          {token.text}
        </code>
      );
    }

    if (token.kind === 'link') {
      const linkClass = tone === 'user'
        ? 'break-all underline decoration-white/50 underline-offset-2 text-white'
        : 'break-all underline decoration-blue-300 underline-offset-2 text-blue-600 hover:text-blue-700';
      const content = token.bold ? <strong>{token.text}</strong> : token.text;
      return (
        <a
          key={index}
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
      return <strong key={index}>{token.text}</strong>;
    }

    return <span key={index}>{token.text}</span>;
  });
}
