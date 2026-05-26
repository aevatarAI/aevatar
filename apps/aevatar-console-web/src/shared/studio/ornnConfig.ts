export interface OrnnRuntimeConfig {
  readonly baseUrl: string;
  readonly configurationError?: string;
}

const DEFAULT_ORNN_BASE_URL = 'https://ornn.chrono-ai.fun';

function trimOptional(value?: string): string | undefined {
  let normalized = value?.trim();
  if (
    normalized &&
    ((normalized.startsWith('"') && normalized.endsWith('"')) ||
      (normalized.startsWith("'") && normalized.endsWith("'")))
  ) {
    normalized = normalized.slice(1, -1).trim();
  }

  if (!normalized) {
    return undefined;
  }

  if (
    normalized.localeCompare('undefined', undefined, {
      sensitivity: 'accent',
    }) === 0 ||
    normalized.localeCompare('null', undefined, {
      sensitivity: 'accent',
    }) === 0
  ) {
    return undefined;
  }

  return normalized;
}

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, '');
}

function resolveWindowOrigin(): string {
  if (typeof window !== 'undefined') {
    return window.location.origin;
  }

  return 'http://127.0.0.1:5173';
}

function isHttpUrl(url: URL): boolean {
  return url.protocol === 'http:' || url.protocol === 'https:';
}

function inferDefaultProtocol(value: string): 'http://' | 'https://' {
  const normalized = value.trim().toLowerCase();
  if (
    normalized.startsWith('localhost') ||
    normalized.startsWith('127.') ||
    normalized.startsWith('0.0.0.0') ||
    normalized.startsWith('10.') ||
    normalized.startsWith('192.168.') ||
    /^172\.(1[6-9]|2\d|3[0-1])([.:/]|$)/.test(normalized)
  ) {
    return 'http://';
  }

  return 'https://';
}

function tryResolveHttpUrl(value: string): string | undefined {
  const normalized = trimOptional(value);
  if (!normalized || normalized.startsWith('//')) {
    return undefined;
  }

  if (normalized.startsWith('/')) {
    return normalizeBaseUrl(new URL(normalized, resolveWindowOrigin()).toString());
  }

  try {
    const absoluteUrl = new URL(normalized);
    if (isHttpUrl(absoluteUrl)) {
      return normalizeBaseUrl(absoluteUrl.toString());
    }
  } catch {
    // Fall through to scheme inference for user-provided hostnames like localhost:3001.
  }

  if (normalized.includes('://')) {
    return undefined;
  }

  try {
    const inferredUrl = new URL(`${inferDefaultProtocol(normalized)}${normalized}`);
    if (!isHttpUrl(inferredUrl)) {
      return undefined;
    }

    return normalizeBaseUrl(inferredUrl.toString());
  } catch {
    return undefined;
  }
}

export function getOrnnRuntimeConfig(): OrnnRuntimeConfig {
  const configuredBaseUrl =
    trimOptional(process.env.ORNN_BASE_URL) ?? DEFAULT_ORNN_BASE_URL;
  const normalizedBaseUrl = tryResolveHttpUrl(configuredBaseUrl);

  return {
    baseUrl: normalizedBaseUrl ?? '',
    configurationError: normalizedBaseUrl
      ? undefined
      : 'ORNN_BASE_URL must be a valid http(s) URL or a root-relative path such as /ornn.',
  };
}

