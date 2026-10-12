import posthog from 'posthog-js';
import { onTTFB } from 'web-vitals';

jest.mock('posthog-js', () => ({
  __esModule: true,
  default: { init: jest.fn(), capture: jest.fn() },
}));
jest.mock('web-vitals', () => ({ onTTFB: jest.fn() }));

function loadAnalytics() {
  let analytics: typeof import('./posthog') | undefined;
  jest.isolateModules(() => {
    analytics = require('./posthog');
  });
  if (!analytics) throw new Error('Analytics module did not load');
  return analytics;
}

function entry(
  duration: number,
  startTime = 120,
  entryType = 'longtask',
): PerformanceEntry {
  return {
    name: 'https://private.example.com/script?token=secret',
    duration,
    startTime,
    entryType,
    toJSON: () => ({ attribution: [{ containerSrc: 'private-frame' }] }),
  };
}

describe('console Long Task reporting', () => {
  const originalEnv = process.env;
  const originalObserver = Object.getOwnPropertyDescriptor(
    window,
    'PerformanceObserver',
  );
  const originalGetEntries = Object.getOwnPropertyDescriptor(
    performance,
    'getEntriesByType',
  );
  let observers: ObserverBoundary[];
  let observeFailure: Error | undefined;
  let bufferedEntries: PerformanceEntry[];
  let navigationEntries: PerformanceEntry[];
  let navigationFailure: Error | undefined;

  class ObserverBoundary implements PerformanceObserver {
    static supportedEntryTypes = ['longtask'];
    readonly observe = jest.fn((options?: PerformanceObserverInit) => {
      if (observeFailure) throw observeFailure;
      if (options?.buffered && bufferedEntries.length) {
        this.deliver(bufferedEntries);
      }
    });
    readonly disconnect = jest.fn();
    readonly takeRecords = jest.fn(() => []);

    constructor(private readonly report: PerformanceObserverCallback) {
      observers.push(this);
    }

    deliver(entries: PerformanceEntry[]) {
      this.report(
        {
          getEntries: () => entries,
          getEntriesByName: (name) =>
            entries.filter((item) => item.name === name),
          getEntriesByType: (type) =>
            entries.filter((item) => item.entryType === type),
        },
        this,
      );
    }
  }

  beforeEach(() => {
    process.env = {
      ...originalEnv,
      AEVATAR_POSTHOG_KEY: 'phc-test',
      AEVATAR_POSTHOG_HOST: 'https://eu.i.posthog.com',
    };
    jest.clearAllMocks();
    jest.mocked(posthog.init).mockReset();
    jest.mocked(posthog.capture).mockReset();
    jest.mocked(onTTFB).mockReset();
    observers = [];
    observeFailure = undefined;
    bufferedEntries = [];
    navigationEntries = [];
    navigationFailure = undefined;
    jest.spyOn(performance, 'now').mockReturnValue(100);
    Object.defineProperty(performance, 'getEntriesByType', {
      configurable: true,
      value: jest.fn((type: string) => {
        if (navigationFailure) throw navigationFailure;
        return type === 'navigation' ? navigationEntries : [];
      }),
    });
    ObserverBoundary.supportedEntryTypes = ['longtask'];
    Object.defineProperty(window, 'PerformanceObserver', {
      configurable: true,
      value: ObserverBoundary,
    });
    window.history.replaceState({}, '', '/scopes/s-alpha/workflows');
  });

  afterEach(() => {
    process.env = originalEnv;
    if (originalObserver) {
      Object.defineProperty(window, 'PerformanceObserver', originalObserver);
    } else {
      Reflect.deleteProperty(window, 'PerformanceObserver');
    }
    if (originalGetEntries) {
      Object.defineProperty(
        performance,
        'getEntriesByType',
        originalGetEntries,
      );
    } else {
      Reflect.deleteProperty(performance, 'getEntriesByType');
    }
  });

  it('reports long task duration and blocking time once against the current sanitized route', () => {
    const analytics = loadAnalytics();
    analytics.initializeConsoleAnalytics();
    analytics.initializeConsoleAnalytics();
    expect(observers).toHaveLength(1);
    expect(observers[0].observe).toHaveBeenCalledWith({
      type: 'longtask',
      buffered: true,
    });

    window.history.replaceState(
      {},
      '',
      '/scopes/s-alpha/workflows/wf-alpha?token=private#secret',
    );
    observers[0].deliver([entry(82.5)]);
    window.history.replaceState({}, '', '/scopes/s-alpha/activity');
    observers[0].deliver([entry(50, 240)]);

    expect(posthog.capture).toHaveBeenNthCalledWith(1, 'long_task', {
      duration_ms: 82.5,
      blocking_time_ms: 32.5,
      start_time_ms: 120,
      $current_url: `${window.location.origin}/scopes/s-alpha/workflows/wf-alpha`,
      $pathname: '/scopes/s-alpha/workflows/wf-alpha',
    });
    expect(posthog.capture).toHaveBeenNthCalledWith(2, 'long_task', {
      duration_ms: 50,
      blocking_time_ms: 0,
      start_time_ms: 240,
      $current_url: `${window.location.origin}/scopes/s-alpha/activity`,
      $pathname: '/scopes/s-alpha/activity',
    });
    expect(posthog.capture).toHaveBeenCalledTimes(2);
  });

  it('recovers buffered startup tasks using the document URL before subsequent SPA tasks', () => {
    navigationEntries = [
      {
        ...entry(0, 0, 'navigation'),
        name: `${window.location.origin}/workflows?token=private#secret`,
      },
    ];
    bufferedEntries = [entry(75, 10)];
    const analytics = loadAnalytics();
    analytics.initializeConsoleAnalytics();
    analytics.initializeConsoleAnalytics();
    expect(posthog.capture).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledWith('long_task', {
      duration_ms: 75,
      blocking_time_ms: 25,
      start_time_ms: 10,
      $current_url: `${window.location.origin}/workflows`,
      $pathname: '/workflows',
    });

    window.history.replaceState(
      {},
      '',
      '/scopes/s-alpha/activity?token=private',
    );
    observers[0].deliver([entry(80, 150)]);
    expect(posthog.capture).toHaveBeenLastCalledWith('long_task', {
      duration_ms: 80,
      blocking_time_ms: 30,
      start_time_ms: 150,
      $current_url: `${window.location.origin}/scopes/s-alpha/activity`,
      $pathname: '/scopes/s-alpha/activity',
    });
  });

  it('retains the initialization URL for early tasks when Navigation Timing fails', () => {
    navigationFailure = new Error('Navigation Timing unavailable');
    window.history.replaceState({}, '', '/workflows?token=private#secret');
    loadAnalytics().initializeConsoleAnalytics();
    window.history.replaceState({}, '', '/scopes/s-alpha/settings');
    observers[0].deliver([entry(65, 20)]);
    expect(posthog.capture).toHaveBeenCalledWith('long_task', {
      duration_ms: 65,
      blocking_time_ms: 15,
      start_time_ms: 20,
      $current_url: `${window.location.origin}/workflows`,
      $pathname: '/workflows',
    });
    expect(onTTFB).toHaveBeenCalledTimes(1);
  });

  it('ignores invalid samples and continues reporting after a collector failure', () => {
    loadAnalytics().initializeConsoleAnalytics();
    jest.mocked(posthog.capture).mockImplementationOnce(() => {
      throw new Error('Collector unavailable');
    });
    expect(() =>
      observers[0].deliver([
        entry(90, 120, 'resource'),
        entry(49),
        entry(Number.NaN),
        entry(Number.POSITIVE_INFINITY),
        entry(90, -1),
        entry(90, Number.NaN),
        entry(90, Number.POSITIVE_INFINITY),
        entry(100),
        entry(140, 320),
      ]),
    ).not.toThrow();
    expect(posthog.capture).toHaveBeenCalledTimes(2);
    expect(posthog.capture).toHaveBeenLastCalledWith('long_task', {
      duration_ms: 140,
      blocking_time_ms: 90,
      start_time_ms: 320,
      $current_url: `${window.location.origin}/scopes/s-alpha/workflows`,
      $pathname: '/scopes/s-alpha/workflows',
    });
  });

  it.each([
    'missing',
    'unsupported',
  ])('keeps analytics usable when the browser API is %s', (availability) => {
    if (availability === 'missing') {
      Reflect.deleteProperty(window, 'PerformanceObserver');
    } else {
      ObserverBoundary.supportedEntryTypes = ['resource'];
    }
    const analytics = loadAnalytics();
    expect(analytics.initializeConsoleAnalytics).not.toThrow();
    analytics.captureConsoleEvent('workflow_started', {
      workflowId: 'wf-alpha',
      nodeCount: 2,
      scopeId: 'scope-alpha',
      invocationId: 'invocation-alpha',
      start_source: 'run_started',
    });
    expect(observers).toHaveLength(0);
    expect(onTTFB).toHaveBeenCalledTimes(1);
    expect(posthog.capture).toHaveBeenCalledWith('workflow_started', {
      workflowId: 'wf-alpha',
      nodeCount: 2,
      scopeId: 'scope-alpha',
      invocationId: 'invocation-alpha',
      start_source: 'run_started',
    });
  });

  it('starts observation only after analytics is configured and initializes successfully', () => {
    const analytics = loadAnalytics();
    delete process.env.AEVATAR_POSTHOG_KEY;
    analytics.initializeConsoleAnalytics();
    expect(posthog.init).not.toHaveBeenCalled();
    expect(observers).toHaveLength(0);
    process.env.AEVATAR_POSTHOG_KEY = 'phc-test';
    jest.mocked(posthog.init).mockImplementationOnce(() => {
      throw new Error('Initialization unavailable');
    });
    expect(analytics.initializeConsoleAnalytics).not.toThrow();
    expect(observers).toHaveLength(0);
    expect(onTTFB).not.toHaveBeenCalled();

    analytics.initializeConsoleAnalytics();
    observers[0].deliver([entry(100)]);
    expect(posthog.capture).toHaveBeenCalledWith(
      'long_task',
      expect.objectContaining({ duration_ms: 100 }),
    );
  });

  it.each([
    'TTFB',
    'Long Task',
  ])('isolates %s registration failure from other telemetry', (failedObserver) => {
    if (failedObserver === 'TTFB') {
      jest.mocked(onTTFB).mockImplementationOnce(() => {
        throw new Error('Navigation Timing unavailable');
      });
    } else {
      observeFailure = new Error('Long Tasks unavailable');
    }
    const analytics = loadAnalytics();
    expect(analytics.initializeConsoleAnalytics).not.toThrow();
    analytics.initializeConsoleAnalytics();
    expect(onTTFB).toHaveBeenCalledTimes(1);
    expect(observers).toHaveLength(1);
    if (failedObserver === 'TTFB') {
      observers[0].deliver([entry(100)]);
      expect(posthog.capture).toHaveBeenCalledWith(
        'long_task',
        expect.objectContaining({ duration_ms: 100 }),
      );
    } else {
      analytics.captureConsoleEvent('workflow_started', {
        workflowId: 'wf-alpha',
        nodeCount: 2,
        scopeId: 'scope-alpha',
        invocationId: 'invocation-alpha',
        start_source: 'run_started',
      });
      expect(posthog.capture).toHaveBeenCalledWith('workflow_started', {
        workflowId: 'wf-alpha',
        nodeCount: 2,
        scopeId: 'scope-alpha',
        invocationId: 'invocation-alpha',
        start_source: 'run_started',
      });
    }
  });
});
