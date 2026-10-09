type BrowserLocationNavigation = Pick<Location, 'assign' | 'replace'>;

function isBrowserLocationNavigation(
  value: unknown,
): value is BrowserLocationNavigation {
  return (
    value !== null &&
    typeof value === 'object' &&
    typeof Reflect.get(value, 'assign') === 'function' &&
    typeof Reflect.get(value, 'replace') === 'function'
  );
}

export function mockBrowserLocationNavigation(
  method: keyof BrowserLocationNavigation,
) {
  const implementationKey = Object.getOwnPropertySymbols(window.location).find(
    (property) => property.description === 'impl',
  );
  const implementation: unknown = implementationKey
    ? Reflect.get(window.location, implementationKey)
    : undefined;

  if (!isBrowserLocationNavigation(implementation)) {
    throw new Error('The pinned jsdom Location implementation is unavailable.');
  }

  return jest.spyOn(implementation, method).mockImplementation(() => {});
}
