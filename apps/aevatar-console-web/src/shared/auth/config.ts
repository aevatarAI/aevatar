import { prefixAppBasePath } from '../navigation/appPath';

export interface NyxIDRuntimeConfig {
  readonly enabled: boolean;
  readonly baseUrl: string;
  readonly clientId: string;
  readonly redirectUri: string;
  readonly scope: string;
  readonly configurationError?: string;
}

const DEFAULT_SCOPE = 'openid profile email';
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

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, '');
}

function resolveWindowOrigin(): string {
  if (typeof window !== 'undefined') {
    return window.location.origin;
  }

  return 'http://127.0.0.1:5173';
}

function resolveDefaultRedirectUri(): string {
  return `${resolveWindowOrigin()}${prefixAppBasePath(DEFAULT_REDIRECT_PATH)}`;
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
  options: { readonly allowRelative: boolean; readonly trimTrailingSlash: boolean },
): string | undefined {
  const normalized = trimOptional(value);
  if (!normalized || normalized.startsWith('//')) {
    return undefined;
  }

  if (options.allowRelative && normalized.startsWith('/')) {
    const relativeUrl = new URL(normalized, resolveWindowOrigin());
    const resolved = relativeUrl.toString();
    return options.trimTrailingSlash ? normalizeBaseUrl(resolved) : resolved;
  }

  try {
    const absoluteUrl = new URL(normalized);
    if (isHttpUrl(absoluteUrl)) {
      const resolved = absoluteUrl.toString();
      return options.trimTrailingSlash ? normalizeBaseUrl(resolved) : resolved;
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

    const resolved = inferredUrl.toString();
    return options.trimTrailingSlash ? normalizeBaseUrl(resolved) : resolved;
  } catch {
    return undefined;
  }
}

function buildConfigurationError(
  variableName: 'NYXID_BASE_URL' | 'NYXID_REDIRECT_URI',
  exampleValue: string,
): string {
  return `${variableName} must be a valid http(s) URL or a root-relative path such as ${exampleValue}.`;
}

function buildMissingConfigurationError(variableName: 'NYXID_BASE_URL' | 'NYXID_CLIENT_ID'): string {
  return `${variableName} must be configured to enable NyxID login.`;
}

export function getNyxIDRuntimeConfig(): NyxIDRuntimeConfig {
  const baseUrl = trimOptional(process.env.NYXID_BASE_URL) ?? '';
  const clientId = trimOptional(process.env.NYXID_CLIENT_ID) ?? '';
  const redirectUri =
    trimOptional(process.env.NYXID_REDIRECT_URI) ?? resolveDefaultRedirectUri();
  const scope = trimOptional(process.env.NYXID_SCOPE) ?? DEFAULT_SCOPE;
  const normalizedBaseUrl = tryResolveHttpUrl(baseUrl, {
    allowRelative: true,
    trimTrailingSlash: true,
  });
  const normalizedRedirectUri = tryResolveHttpUrl(redirectUri, {
    allowRelative: true,
    trimTrailingSlash: false,
  });
  const configurationError =
    clientId.length === 0
      ? buildMissingConfigurationError('NYXID_CLIENT_ID')
      : baseUrl.length === 0
      ? buildMissingConfigurationError('NYXID_BASE_URL')
        : !normalizedBaseUrl
      ? buildConfigurationError('NYXID_BASE_URL', '/nyxid')
      : !normalizedRedirectUri
        ? buildConfigurationError(
            'NYXID_REDIRECT_URI',
            prefixAppBasePath(DEFAULT_REDIRECT_PATH),
          )
        : undefined;

  return {
    enabled:
      clientId.length > 0 &&
      Boolean(normalizedBaseUrl) &&
      Boolean(normalizedRedirectUri),
    baseUrl: normalizedBaseUrl ?? '',
    clientId,
    redirectUri: normalizedRedirectUri ?? '',
    scope,
    configurationError,
  };
}
