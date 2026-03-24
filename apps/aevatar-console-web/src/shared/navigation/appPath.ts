const DEFAULT_APP_BASE_PATH = '/';

export function normalizeAppBasePath(value?: string): string {
  const normalized = value?.trim();
  if (!normalized || normalized === '/') {
    return DEFAULT_APP_BASE_PATH;
  }

  const rootRelative = normalized.startsWith('/') ? normalized : `/${normalized}`;
  return rootRelative.endsWith('/') ? rootRelative : `${rootRelative}/`;
}

export function getConfiguredAppBasePath(): string {
  return normalizeAppBasePath(process.env.AEVATAR_CONSOLE_PUBLIC_PATH);
}

export function stripAppBasePath(
  pathname: string,
  basePath = getConfiguredAppBasePath(),
): string {
  const normalizedPathname = normalizePathname(pathname);
  const basePrefix = getBasePrefix(basePath);
  if (!basePrefix) {
    return normalizedPathname;
  }

  if (
    normalizedPathname === basePrefix ||
    normalizedPathname === `${basePrefix}/`
  ) {
    return '/';
  }

  if (normalizedPathname.startsWith(`${basePrefix}/`)) {
    return normalizedPathname.slice(basePrefix.length) || '/';
  }

  return normalizedPathname;
}

export function prefixAppBasePath(
  target: string,
  basePath = getConfiguredAppBasePath(),
): string {
  const normalizedTarget = target.trim();
  if (!normalizedTarget) {
    return basePath;
  }

  const { pathname, suffix } = splitPathSuffix(normalizedTarget);
  const normalizedPathname = normalizePathname(pathname);
  const basePrefix = getBasePrefix(basePath);
  if (!basePrefix) {
    return `${normalizedPathname}${suffix}`;
  }

  if (
    normalizedPathname === basePrefix ||
    normalizedPathname.startsWith(`${basePrefix}/`)
  ) {
    return `${normalizedPathname}${suffix}`;
  }

  if (normalizedPathname === '/') {
    return `${basePrefix}/${suffix}`;
  }

  return `${basePrefix}${normalizedPathname}${suffix}`;
}

export function getCurrentAppPathname(
  basePath = getConfiguredAppBasePath(),
): string {
  if (typeof window === 'undefined') {
    return '/';
  }

  return stripAppBasePath(window.location.pathname, basePath);
}

export function resolveAppHref(
  target: string,
  basePath = getConfiguredAppBasePath(),
): string {
  const normalizedTarget = target.trim();
  if (!normalizedTarget) {
    return prefixAppBasePath('/', basePath);
  }

  if (normalizedTarget.startsWith('//')) {
    return normalizedTarget;
  }

  if (hasScheme(normalizedTarget)) {
    return normalizedTarget;
  }

  if (
    normalizedTarget.startsWith('/') ||
    normalizedTarget.startsWith('?') ||
    normalizedTarget.startsWith('#')
  ) {
    return prefixAppBasePath(
      buildRouteFromCurrentPath(normalizedTarget, basePath),
      basePath,
    );
  }

  return prefixAppBasePath(resolveRelativeRoute(normalizedTarget, basePath), basePath);
}

export function replaceAppLocation(
  target: string,
  basePath = getConfiguredAppBasePath(),
): void {
  if (typeof window === 'undefined') {
    return;
  }

  window.location.replace(resolveAppHref(target, basePath));
}

function buildRouteFromCurrentPath(target: string, basePath: string): string {
  if (target.startsWith('/')) {
    return target;
  }

  const currentPath = getCurrentAppPathname(basePath);
  return `${currentPath}${target}`;
}

function resolveRelativeRoute(target: string, basePath: string): string {
  const currentPath = getCurrentAppPathname(basePath);
  const currentDirectory = currentPath.endsWith('/')
    ? currentPath
    : currentPath.slice(0, currentPath.lastIndexOf('/') + 1);
  const resolved = new URL(target, `http://localhost${currentDirectory}`);
  return `${resolved.pathname}${resolved.search}${resolved.hash}`;
}

function splitPathSuffix(target: string): {
  pathname: string;
  suffix: string;
} {
  const queryIndex = target.indexOf('?');
  const hashIndex = target.indexOf('#');
  const splitIndex =
    queryIndex === -1
      ? hashIndex
      : hashIndex === -1
        ? queryIndex
        : Math.min(queryIndex, hashIndex);

  if (splitIndex === -1) {
    return {
      pathname: target,
      suffix: '',
    };
  }

  return {
    pathname: target.slice(0, splitIndex),
    suffix: target.slice(splitIndex),
  };
}

function normalizePathname(pathname: string): string {
  const normalized = pathname.trim();
  if (!normalized) {
    return '/';
  }

  return normalized.startsWith('/') ? normalized : `/${normalized}`;
}

function getBasePrefix(basePath: string): string {
  return basePath === '/' ? '' : basePath.slice(0, -1);
}

function hasScheme(value: string): boolean {
  return /^[a-zA-Z][a-zA-Z\d+.-]*:/.test(value);
}
