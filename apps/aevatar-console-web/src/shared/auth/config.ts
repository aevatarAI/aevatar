export interface NyxIDRuntimeConfig {
  readonly enabled: boolean;
  readonly baseUrl: string;
  readonly clientId: string;
  readonly redirectUri: string;
  readonly scope: string;
  readonly configurationError?: string;
}

const DEFAULT_REDIRECT_PATH = '/auth/callback';

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

  return normalized ? normalized : undefined;
}

function resolveWindowOrigin(): string {
  if (typeof window !== 'undefined') {
    return window.location.origin;
  }

  return 'http://127.0.0.1:5173';
}

function resolveDefaultRedirectUri(): string {
  return `${resolveWindowOrigin()}${DEFAULT_REDIRECT_PATH}`;
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

function tryResolveHttpUrl(
  value: string,
  options: { readonly allowRelative: boolean },
): string | undefined {
  const normalized = trimOptional(value);
  if (!normalized || normalized.startsWith('//')) {
    return undefined;
  }

  if (options.allowRelative && normalized.startsWith('/')) {
    const relativeUrl = new URL(normalized, resolveWindowOrigin());
    return relativeUrl.toString();
  }

  try {
    const absoluteUrl = new URL(normalized);
    if (isHttpUrl(absoluteUrl)) {
      return absoluteUrl.toString();
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

    return inferredUrl.toString();
  } catch {
    return undefined;
  }
}

function buildConfigurationError(
  variableName: 'NYXID_REDIRECT_URI',
  exampleValue: string,
): string {
  return `${variableName} must be a valid http(s) URL or a root-relative path such as ${exampleValue}.`;
}

export function getNyxIDRuntimeConfig(): NyxIDRuntimeConfig {
  const redirectUri =
    trimOptional(process.env.NYXID_REDIRECT_URI) ?? resolveDefaultRedirectUri();
  const normalizedRedirectUri = tryResolveHttpUrl(redirectUri, {
    allowRelative: true,
  });
  const configurationError =
    !normalizedRedirectUri
        ? buildConfigurationError('NYXID_REDIRECT_URI', '/auth/callback')
        : undefined;

  return {
    enabled: Boolean(normalizedRedirectUri),
    baseUrl: '',
    clientId: '',
    redirectUri: normalizedRedirectUri ?? '',
    scope: '',
    configurationError,
  };
}
