const SCRIPT_PACKAGE_FORMAT = "aevatar.scripting.package.v1";

export type ExplorerChatMessage = {
  id: string;
  role: string;
  content: string;
  timestamp: number;
  status: string;
  error?: string;
  thinking?: string;
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

export type ExplorerContentModel =
  | {
      kind: "chat-history";
      messages: ExplorerChatMessage[];
    }
  | {
      kind: "script-package";
      package: ExplorerScriptPackage;
    }
  | {
      kind: "json";
      formattedText: string;
    }
  | {
      kind: "text";
      formattedText: string;
    };

export function buildExplorerContentModel(
  fileType: string,
  content: string
): ExplorerContentModel {
  const normalized = normalizeLineEndings(content);

  if (fileType === "chat-history") {
    const messages = tryParseChatHistory(normalized);
    if (messages) {
      return {
        kind: "chat-history",
        messages,
      };
    }
  }

  if (fileType === "script") {
    const scriptPackage = tryParseScriptPackage(normalized);
    if (scriptPackage) {
      return {
        kind: "script-package",
        package: scriptPackage,
      };
    }
  }

  const formattedJson = tryFormatJson(normalized);
  if (formattedJson) {
    return {
      kind: "json",
      formattedText: formattedJson,
    };
  }

  return {
    kind: "text",
    formattedText: normalized,
  };
}

function normalizeLineEndings(content: string): string {
  return content.replace(/\r\n?/g, "\n");
}

function tryParseChatHistory(content: string): ExplorerChatMessage[] | null {
  const trimmed = content.trim();
  if (!trimmed) {
    return [];
  }

  const wholeJson = tryParseJsonValue(trimmed);
  if (Array.isArray(wholeJson)) {
    const fromArray = wholeJson
      .map((item) => normalizeChatMessage(item))
      .filter((item): item is ExplorerChatMessage => item !== null);
    if (fromArray.length === wholeJson.length) {
      return fromArray;
    }
  }

  const lines = trimmed
    .split("\n")
    .map((line) => line.trim())
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

  const role = readString(value, ["role", "Role"]);
  const content = readString(value, ["content", "Content"]);
  if (!role || content === null) {
    return null;
  }

  return {
    id: readString(value, ["id", "Id"]) ?? "",
    role,
    content,
    timestamp: readNumber(value, ["timestamp", "Timestamp"]),
    status: readString(value, ["status", "Status"]) ?? "complete",
    error: readString(value, ["error", "Error"]) ?? undefined,
    thinking: readString(value, ["thinking", "Thinking"]) ?? undefined,
  };
}

function tryParseScriptPackage(content: string): ExplorerScriptPackage | null {
  const parsed = tryParseJsonValue(content);
  if (!isRecord(parsed)) {
    return null;
  }

  const format = readString(parsed, ["format", "Format"]) ?? "";
  const csharpSources = readFiles(
    parsed,
    ["cSharpSources", "csharpSources", "CSharpSources"],
    "Behavior.cs"
  );
  const protoFiles = readFiles(parsed, ["protoFiles", "ProtoFiles"], "schema.proto");

  if (
    format !== SCRIPT_PACKAGE_FORMAT &&
    csharpSources.length === 0 &&
    protoFiles.length === 0
  ) {
    return null;
  }

  return {
    format: format || SCRIPT_PACKAGE_FORMAT,
    entryBehaviorTypeName:
      readString(parsed, ["entryBehaviorTypeName", "EntryBehaviorTypeName"]) ?? "",
    entrySourcePath: readString(parsed, ["entrySourcePath", "EntrySourcePath"]) ?? "",
    csharpSources,
    protoFiles,
  };
}

function readFiles(
  source: Record<string, unknown>,
  keys: string[],
  fallbackPath: string
): ExplorerScriptFile[] {
  const value = readValue(source, keys);
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((item, index) => normalizeScriptFile(item, `${fallbackPath}-${index + 1}`))
    .filter((item): item is ExplorerScriptFile => item !== null);
}

function normalizeScriptFile(
  value: unknown,
  fallbackPath: string
): ExplorerScriptFile | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    path: readString(value, ["path", "Path"]) ?? fallbackPath,
    content: readString(value, ["content", "Content"]) ?? "",
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
  return typeof value === "string" ? value : null;
}

function readNumber(source: Record<string, unknown>, keys: string[]): number {
  const value = readValue(source, keys);
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  const numeric = Number(value);
  return Number.isFinite(numeric) ? numeric : 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
