const STRING_VALUE_TYPE_URL =
  "type.googleapis.com/google.protobuf.StringValue";
const APP_SCRIPT_COMMAND_TYPE_URL =
  "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand";
const TYPE_URL_PREFIX = "type.googleapis.com/";

function encodeVarint(value: number): number[] {
  let remaining = value >>> 0;
  const bytes: number[] = [];

  while (remaining >= 0x80) {
    bytes.push((remaining & 0x7f) | 0x80);
    remaining >>>= 7;
  }

  bytes.push(remaining);
  return bytes;
}

function bytesToBase64(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") {
    return Buffer.from(bytes).toString("base64");
  }

  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return globalThis.btoa(binary);
}

function encodeUtf8(value: string): Uint8Array {
  return typeof TextEncoder !== "undefined"
    ? new TextEncoder().encode(value)
    : Buffer.from(value, "utf8");
}

function appendStringField(bytes: number[], fieldNumber: number, value: string): void {
  if (!value) {
    return;
  }

  const encodedText = encodeUtf8(value);
  bytes.push((fieldNumber << 3) | 2);
  bytes.push(...encodeVarint(encodedText.length));
  bytes.push(...encodedText);
}

export function getStringValueTypeUrl(): string {
  return STRING_VALUE_TYPE_URL;
}

export function encodeStringValueBase64(value: string): string {
  const bytes: number[] = [];
  appendStringField(bytes, 1, value);
  return bytesToBase64(Uint8Array.from(bytes));
}

export function getAppScriptCommandTypeUrl(): string {
  return APP_SCRIPT_COMMAND_TYPE_URL;
}

export function getAppScriptCommandEndpointId(): string {
  return APP_SCRIPT_COMMAND_TYPE_URL.replace(TYPE_URL_PREFIX, "");
}

export function normalizeTypeUrlEndpointId(typeUrl: string): string {
  const normalized = typeUrl.trim();
  if (!normalized) {
    return "";
  }

  return normalized.startsWith(TYPE_URL_PREFIX)
    ? normalized.slice(TYPE_URL_PREFIX.length)
    : normalized;
}

export function encodeAppScriptCommandBase64(value: {
  commandId: string;
  input: string;
}): string {
  const bytes: number[] = [];
  appendStringField(bytes, 1, value.commandId);
  appendStringField(bytes, 2, value.input);
  return bytesToBase64(Uint8Array.from(bytes));
}

export function encodeTextPayloadBase64(
  typeUrl: string,
  value: string,
  options?: {
    commandId?: string;
  }
): string {
  const normalizedTypeUrl = typeUrl.trim();
  if (normalizedTypeUrl === APP_SCRIPT_COMMAND_TYPE_URL) {
    return encodeAppScriptCommandBase64({
      commandId: options?.commandId?.trim() ?? "",
      input: value,
    });
  }

  return encodeStringValueBase64(value);
}

export const typeUrlToEndpointId = normalizeTypeUrlEndpointId;
