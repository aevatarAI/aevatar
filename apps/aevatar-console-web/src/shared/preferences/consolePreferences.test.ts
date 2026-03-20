import {
  defaultConsolePreferences,
  loadConsolePreferences,
  resetConsolePreferences,
  saveConsolePreferences,
} from './consolePreferences';

describe('consolePreferences', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('loads defaults when storage is empty and persists sanitized values', () => {
    expect(loadConsolePreferences()).toEqual(defaultConsolePreferences);

    const saved = saveConsolePreferences({
      preferredWorkflow: '  human_input_manual_triage  ',
      actorTimelineTake: 30.9,
      actorGraphDepth: 2,
      actorGraphTake: 80,
      actorGraphDirection: 'Outbound',
      studioAppearanceTheme: 'forest',
      studioColorMode: 'dark',
      grafanaBaseUrl: '  https://grafana.example.com  ',
      jaegerBaseUrl: ' https://jaeger.example.com ',
      lokiBaseUrl: ' https://loki.example.com ',
    });

    expect(saved).toEqual({
      preferredWorkflow: 'human_input_manual_triage',
      actorTimelineTake: 30,
      actorGraphDepth: 2,
      actorGraphTake: 80,
      actorGraphDirection: 'Outbound',
      studioAppearanceTheme: 'forest',
      studioColorMode: 'dark',
      grafanaBaseUrl: 'https://grafana.example.com',
      jaegerBaseUrl: 'https://jaeger.example.com',
      lokiBaseUrl: 'https://loki.example.com',
    });

    expect(loadConsolePreferences()).toEqual(saved);
    expect(resetConsolePreferences()).toEqual(defaultConsolePreferences);
    expect(loadConsolePreferences()).toEqual(defaultConsolePreferences);
  });
});
