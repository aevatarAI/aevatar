import { defaultConfig } from 'antd/lib/theme/internal';

defaultConfig.hashed = false;

const localStorageState = new Map();

const localStorageMock = {
  get length() {
    return localStorageState.size;
  },
  clear: jest.fn(() => {
    localStorageState.clear();
  }),
  getItem: jest.fn((key) => {
    const normalizedKey = String(key);
    return localStorageState.has(normalizedKey)
      ? localStorageState.get(normalizedKey)
      : null;
  }),
  key: jest.fn((index) => Array.from(localStorageState.keys())[index] ?? null),
  removeItem: jest.fn((key) => {
    localStorageState.delete(String(key));
  }),
  setItem: jest.fn((key, value) => {
    localStorageState.set(String(key), String(value));
  }),
};

global.localStorage = localStorageMock;
Object.defineProperty(window, 'localStorage', {
  configurable: true,
  value: localStorageMock,
});

Object.defineProperty(URL, 'createObjectURL', {
  writable: true,
  value: jest.fn(),
});

class Worker {
  constructor(stringUrl) {
    this.url = stringUrl;
    this.onmessage = () => {};
  }

  postMessage(msg) {
    this.onmessage(msg);
  }
}
window.Worker = Worker;
// Polyfill MessageChannel for environments (like jest/jsdom) that don't provide it
if (typeof global.MessageChannel === 'undefined') {
  class PolyMessageChannel {
    constructor() {
      const channel = this;
      this.port1 = {
        postMessage(msg) {
          setTimeout(() => {
            if (
              channel.port2 &&
              typeof channel.port2.onmessage === 'function'
            ) {
              channel.port2.onmessage({ data: msg });
            }
          }, 0);
        },
      };
      this.port2 = {
        postMessage(msg) {
          setTimeout(() => {
            if (
              channel.port1 &&
              typeof channel.port1.onmessage === 'function'
            ) {
              channel.port1.onmessage({ data: msg });
            }
          }, 0);
        },
      };
    }
  }

  global.MessageChannel = PolyMessageChannel;
  if (typeof window !== 'undefined') {
    window.MessageChannel = PolyMessageChannel;
  }
}

if (typeof window !== 'undefined') {
  // ref: https://github.com/ant-design/ant-design/issues/18774
  if (!window.matchMedia) {
    Object.defineProperty(global.window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: jest.fn(() => ({
        matches: false,
        addListener: jest.fn(),
        removeListener: jest.fn(),
      })),
    });
  }
  if (!window.matchMedia) {
    Object.defineProperty(global.window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: jest.fn((query) => ({
        matches: query.includes('max-width'),
        addListener: jest.fn(),
        removeListener: jest.fn(),
      })),
    });
  }
}
const ignoredConsoleErrors = [
  'Warning: An update to %s inside a test was not wrapped in act(...)',
  'Warning: [antd: Space] `direction` is deprecated. Please use `orientation` instead.',
  'Warning: [antd: List] The `List` component is deprecated. And will be removed in next major version.',
  'Warning: [antd: Alert] `message` is deprecated. Please use `title` instead.',
];

const errorLog = console.error;
Object.defineProperty(global.window.console, 'error', {
  writable: true,
  configurable: true,
  value: (...rest) => {
    const logStr = rest.join('');
    if (ignoredConsoleErrors.some((message) => logStr.includes(message))) {
      return;
    }
    errorLog(...rest);
  },
});

// Mock ResizeObserver
global.ResizeObserver = class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
};
