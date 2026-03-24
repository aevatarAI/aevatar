import {
  normalizeAppBasePath,
  prefixAppBasePath,
  resolveAppHref,
  stripAppBasePath,
} from './appPath';

describe('appPath', () => {
  it('normalizes the configured base path into a root-relative trailing-slash form', () => {
    expect(normalizeAppBasePath()).toBe('/');
    expect(normalizeAppBasePath('console')).toBe('/console/');
    expect(normalizeAppBasePath('/console')).toBe('/console/');
  });

  it('strips the configured base path from browser locations', () => {
    expect(stripAppBasePath('/console/overview', '/console/')).toBe('/overview');
    expect(stripAppBasePath('/console/', '/console/')).toBe('/');
  });

  it('prefixes internal routes with the configured base path', () => {
    expect(prefixAppBasePath('/overview', '/console/')).toBe('/console/overview');
    expect(prefixAppBasePath('/login?redirect=%2Foverview', '/console/')).toBe(
      '/console/login?redirect=%2Foverview',
    );
  });

  it('resolves same-app routes against the configured base path', () => {
    expect(resolveAppHref('/overview', '/console/')).toBe('/console/overview');
    expect(resolveAppHref('/auth/callback', '/console/')).toBe(
      '/console/auth/callback',
    );
  });
});
