import {
  type ConsoleMessages,
  createLocaleCatalogLoader,
} from './createLocaleCatalogLoader';

describe('locale catalog loading', () => {
  it('loads only the selected language and registers it before completing', async () => {
    const en = { greeting: 'Hello' };
    const zh = { greeting: '你好' };
    const imports = {
      'en-US': jest.fn(async () => ({ default: en })),
      'zh-CN': jest.fn(async () => ({ default: zh })),
    };
    const registered = new Map<string, ConsoleMessages>();
    const load = createLocaleCatalogLoader(imports, (locale, messages) => {
      registered.set(locale, messages);
    });

    await load('zh-CN');
    expect(registered.get('zh-CN')).toEqual(zh);
    expect(registered.has('en-US')).toBe(false);
    expect(imports['en-US']).not.toHaveBeenCalled();
  });

  it('shares pending requests and reuses a successfully registered catalog', async () => {
    let finish!: (value: { default: ConsoleMessages }) => void;
    const request = new Promise<{ default: ConsoleMessages }>((resolve) => {
      finish = resolve;
    });
    const imports = {
      'en-US': jest.fn(() => request),
      'zh-CN': jest.fn(async () => ({ default: { greeting: '你好' } })),
    };
    const register = jest.fn();
    const load = createLocaleCatalogLoader(imports, register);
    const first = load('en-US');
    const second = load('en-US');
    expect(register).not.toHaveBeenCalled();

    finish({ default: { greeting: 'Hello' } });
    await Promise.all([first, second]);
    await load('en-US');
    expect(imports['en-US']).toHaveBeenCalledTimes(1);
    expect(register).toHaveBeenCalledTimes(1);
  });

  it('keeps the existing language usable when a download fails and permits retry', async () => {
    const imports = {
      'en-US': jest.fn(async () => ({ default: { greeting: 'Hello' } })),
      'zh-CN': jest
        .fn<Promise<{ default: ConsoleMessages }>, []>()
        .mockRejectedValueOnce(new Error('Network unavailable'))
        .mockResolvedValue({ default: { greeting: '你好' } }),
    };
    const registered = new Map<string, ConsoleMessages>();
    const load = createLocaleCatalogLoader(imports, (locale, messages) => {
      registered.set(locale, messages);
    });
    await load('en-US');
    await expect(load('zh-CN')).rejects.toThrow('Network unavailable');
    expect(registered.get('en-US')).toEqual({ greeting: 'Hello' });
    expect(registered.has('zh-CN')).toBe(false);

    await load('zh-CN');
    expect(registered.get('zh-CN')).toEqual({ greeting: '你好' });
    expect(imports['zh-CN']).toHaveBeenCalledTimes(2);
  });

  it('does not cache a catalog whose registration failed', async () => {
    const imports = {
      'en-US': async () => ({ default: { greeting: 'Hello' } }),
      'zh-CN': async () => ({ default: { greeting: '你好' } }),
    };
    const register = jest.fn().mockImplementationOnce(() => {
      throw new Error('Registration failed');
    });
    const load = createLocaleCatalogLoader(imports, register);
    await expect(load('en-US')).rejects.toThrow('Registration failed');
    await expect(load('en-US')).resolves.toBeUndefined();
    expect(register).toHaveBeenCalledTimes(2);
  });
});
