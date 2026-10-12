export type ConsoleCatalogLocale = 'en-US' | 'zh-CN';
export type ConsoleMessages = Readonly<Record<string, string>>;
type CatalogImport = () => Promise<{ default: ConsoleMessages }>;

/** Share in-flight imports and allow a failed chunk request to be retried. */
export function createLocaleCatalogLoader(
  imports: Record<ConsoleCatalogLocale, CatalogImport>,
  register: (locale: ConsoleCatalogLocale, messages: ConsoleMessages) => void,
) {
  const pending = new Map<ConsoleCatalogLocale, Promise<void>>();

  return (locale: ConsoleCatalogLocale): Promise<void> => {
    const existing = pending.get(locale);
    if (existing) return existing;

    const request = imports[locale]()
      .then(({ default: messages }) => register(locale, messages))
      .catch((error: unknown) => {
        pending.delete(locale);
        throw error;
      });
    pending.set(locale, request);
    return request;
  };
}
