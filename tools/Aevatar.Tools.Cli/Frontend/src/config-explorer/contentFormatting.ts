const SCRIPT_PACKAGE_FORMAT = 'aevatar.scripting.package.v1';

export type ExplorerAttachment = {
  id: string;
  name: string;
  mediaType: string;
  size: number;
  storageKey: string;
};

export type ExplorerMediaPart = {
  type: 'text' | 'image' | 'audio' | 'video';
  text?: string;
  dataBase64?: string;
  mediaType?: string;
  uri?: string;
  name?: string;
};

export type ExplorerChatMessage = {
  id: string;
  role: string;
  content: string;
  timestamp: number;
  status: string;
  error?: string;
  thinking?: string;
  attachments?: ExplorerAttachment[];
  mediaParts?: ExplorerMediaPart[];
};

export type ExplorerScriptFile = {
  path: string;
  content: string;
};

export type ExplorerScriptPackage = {
  format: string;
  entryBehaviorTypeName: string;
  entrySourcePath: string;
  csharpSources: ExplorerScriptFile[];
  protoFiles: ExplorerScriptFile[];
};

export type MediaFileKind = 'image' | 'audio' | 'video' | 'pdf' | 'markdown';

export type ExplorerContentModel =
  | {
      kind: 'chat-history';
      messages: ExplorerChatMessage[];
    }
  | {
      kind: 'script-package';
      package: ExplorerScriptPackage;
    }
  | {
      kind: 'json';
      formattedText: string;
    }
  | {
      kind: 'text';
      formattedText: string;
    };

export function buildExplorerContentModel(fileType: string, content: string): ExplorerContentModel {
  const normalized = normalizeLineEndings(content);

  if (fileType === 'chat-history') {
    const messages = tryParseChatHistory(normalized);
    if (messages) {
      return {
        kind: 'chat-history',
        messages,
      };
    }
  }

  if (fileType === 'script') {
    const scriptPackage = tryParseScriptPackage(normalized);
    if (scriptPackage) {
      return {
        kind: 'script-package',
        package: scriptPackage,
      };
    }
  }

  const formattedJson = tryFormatJson(normalized);
  if (formattedJson) {
    return {
      kind: 'json',
      formattedText: formattedJson,
    };
  }

  return {
    kind: 'text',
    formattedText: normalized,
  };
}

function normalizeLineEndings(content: string): string {
  return content.replace(/\r\n?/g, '\n');
}

function tryParseChatHistory(content: string): ExplorerChatMessage[] | null {
  const trimmed = content.trim();
  if (!trimmed) {
    return [];
  }

  const wholeJson = tryParseJsonValue(trimmed);
  if (Array.isArray(wholeJson)) {
    const fromArray = wholeJson
      .map(item => normalizeChatMessage(item))
      .filter((item): item is ExplorerChatMessage => item !== null);
    if (fromArray.length === wholeJson.length) {
      return fromArray;
    }
  }

  const lines = trimmed
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean);

  if (!lines.length) {
    return [];
  }

  const messages: ExplorerChatMessage[] = [];
  for (const line of lines) {
    const parsed = tryParseJsonValue(line);
    const normalizedMessage = normalizeChatMessage(parsed);
    if (!normalizedMessage) {
      return null;
    }

    messages.push(normalizedMessage);
  }

  return messages;
}

function normalizeChatMessage(value: unknown): ExplorerChatMessage | null {
  if (!isRecord(value)) {
    return null;
  }

  const role = readString(value, ['role', 'Role']);
  const content = readString(value, ['content', 'Content']);
  if (!role || content === null) {
    return null;
  }

  const rawAttachments = readValue(value, ['attachments', 'Attachments']);
  const attachments = Array.isArray(rawAttachments)
    ? rawAttachments
        .filter(isRecord)
        .map(a => ({
          id: readString(a, ['id', 'Id']) ?? '',
          name: readString(a, ['name', 'Name']) ?? '',
          mediaType: readString(a, ['mediaType', 'MediaType']) ?? 'application/octet-stream',
          size: readNumber(a, ['size', 'Size']),
          storageKey: readString(a, ['storageKey', 'StorageKey']) ?? '',
        }))
        .filter(a => a.storageKey)
    : undefined;

  const rawMediaParts = readValue(value, ['mediaParts', 'MediaParts']);
  const mediaParts = Array.isArray(rawMediaParts)
    ? rawMediaParts
        .filter(isRecord)
        .map(p => ({
          type: (readString(p, ['type', 'Type']) ?? 'text') as ExplorerMediaPart['type'],
          text: readString(p, ['text', 'Text']) ?? undefined,
          dataBase64: readString(p, ['dataBase64', 'DataBase64']) ?? undefined,
          mediaType: readString(p, ['mediaType', 'MediaType']) ?? undefined,
          uri: readString(p, ['uri', 'Uri']) ?? undefined,
          name: readString(p, ['name', 'Name']) ?? undefined,
        }))
    : undefined;

  return {
    id: readString(value, ['id', 'Id']) ?? '',
    role,
    content,
    timestamp: readNumber(value, ['timestamp', 'Timestamp']),
    status: readString(value, ['status', 'Status']) ?? 'complete',
    error: readString(value, ['error', 'Error']) ?? undefined,
    thinking: readString(value, ['thinking', 'Thinking']) ?? undefined,
    ...(attachments?.length ? { attachments } : {}),
    ...(mediaParts?.length ? { mediaParts } : {}),
  };
}

function tryParseScriptPackage(content: string): ExplorerScriptPackage | null {
  const parsed = tryParseJsonValue(content);
  if (!isRecord(parsed)) {
    return null;
  }

  const format = readString(parsed, ['format', 'Format']) ?? '';
  const csharpSources = readFiles(parsed, ['cSharpSources', 'csharpSources', 'CSharpSources'], 'Behavior.cs');
  const protoFiles = readFiles(parsed, ['protoFiles', 'ProtoFiles'], 'schema.proto');

  if (format !== SCRIPT_PACKAGE_FORMAT && csharpSources.length === 0 && protoFiles.length === 0) {
    return null;
  }

  return {
    format: format || SCRIPT_PACKAGE_FORMAT,
    entryBehaviorTypeName: readString(parsed, ['entryBehaviorTypeName', 'EntryBehaviorTypeName']) ?? '',
    entrySourcePath: readString(parsed, ['entrySourcePath', 'EntrySourcePath']) ?? '',
    csharpSources,
    protoFiles,
  };
}

function readFiles(source: Record<string, unknown>, keys: string[], fallbackPath: string): ExplorerScriptFile[] {
  const value = readValue(source, keys);
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((item, index) => normalizeScriptFile(item, `${fallbackPath}-${index + 1}`))
    .filter((item): item is ExplorerScriptFile => item !== null);
}

function normalizeScriptFile(value: unknown, fallbackPath: string): ExplorerScriptFile | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    path: readString(value, ['path', 'Path']) ?? fallbackPath,
    content: readString(value, ['content', 'Content']) ?? '',
  };
}

function tryFormatJson(content: string): string | null {
  const trimmed = content.trim();
  if (!trimmed) {
    return null;
  }

  const parsed = tryParseJsonValue(trimmed);
  if (parsed === undefined) {
    return null;
  }

  try {
    return JSON.stringify(parsed, null, 2);
  } catch {
    return null;
  }
}

function tryParseJsonValue(content: string): unknown {
  try {
    return JSON.parse(content);
  } catch {
    return undefined;
  }
}

function readValue(source: Record<string, unknown>, keys: string[]): unknown {
  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(source, key)) {
      return source[key];
    }
  }

  return undefined;
}

function readString(source: Record<string, unknown>, keys: string[]): string | null {
  const value = readValue(source, keys);
  return typeof value === 'string' ? value : null;
}

function readNumber(source: Record<string, unknown>, keys: string[]): number {
  const value = readValue(source, keys);
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  const numeric = Number(value);
  return Number.isFinite(numeric) ? numeric : 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/* ─── Media file detection ─── */

const IMAGE_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'ico', 'avif']);
const AUDIO_EXTENSIONS = new Set(['mp3', 'wav', 'ogg', 'flac', 'aac', 'm4a', 'wma', 'opus', 'webm']);
const VIDEO_EXTENSIONS = new Set(['mp4', 'webm', 'mov', 'avi', 'mkv', 'ogv', 'm4v']);
const PDF_EXTENSIONS = new Set(['pdf']);
const MARKDOWN_EXTENSIONS = new Set(['md', 'markdown', 'mdx']);

export function detectMediaKind(fileKey: string): MediaFileKind | null {
  const ext = fileKey.split('.').pop()?.toLowerCase();
  if (!ext) return null;
  if (IMAGE_EXTENSIONS.has(ext)) return 'image';
  if (AUDIO_EXTENSIONS.has(ext)) return 'audio';
  if (VIDEO_EXTENSIONS.has(ext)) return 'video';
  if (PDF_EXTENSIONS.has(ext)) return 'pdf';
  if (MARKDOWN_EXTENSIONS.has(ext)) return 'markdown';
  return null;
}

const MIME_MAP: Record<string, string> = {
  png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif',
  webp: 'image/webp', svg: 'image/svg+xml', bmp: 'image/bmp', ico: 'image/x-icon', avif: 'image/avif',
  mp3: 'audio/mpeg', wav: 'audio/wav', ogg: 'audio/ogg', flac: 'audio/flac',
  aac: 'audio/aac', m4a: 'audio/mp4', wma: 'audio/x-ms-wma', opus: 'audio/opus',
  mp4: 'video/mp4', mov: 'video/quicktime', avi: 'video/x-msvideo', mkv: 'video/x-matroska',
  ogv: 'video/ogg', m4v: 'video/mp4',
  pdf: 'application/pdf',
  md: 'text/markdown', markdown: 'text/markdown', mdx: 'text/markdown',
};

export function getMimeType(fileKey: string): string {
  const ext = fileKey.split('.').pop()?.toLowerCase() ?? '';
  return MIME_MAP[ext] ?? 'application/octet-stream';
}
