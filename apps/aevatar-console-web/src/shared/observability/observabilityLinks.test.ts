import type { ConsolePreferences } from '@/shared/preferences/consolePreferences';
import {
  buildObservabilityTargets,
  normalizeBaseUrl,
  type ObservabilityContext,
} from './observabilityLinks';

const preferences: ConsolePreferences = {
  preferredWorkflow: 'direct',
  actorTimelineTake: 50,
  actorGraphDepth: 3,
  actorGraphTake: 100,
  actorGraphDirection: 'Both',
  studioAppearanceTheme: 'blue',
  studioColorMode: 'light',
  grafanaBaseUrl: 'https://grafana.example.com/',
  jaegerBaseUrl: ' https://jaeger.example.com ',
  lokiBaseUrl: '',
};

const context: ObservabilityContext = {
  workflow: 'direct',
  actorId: 'actor-1',
  commandId: 'cmd-1',
  runId: '',
  stepId: '',
};

describe('observabilityLinks', () => {
  it('normalizes base urls and builds target links', () => {
    expect(normalizeBaseUrl(' https://grafana.example.com/ ')).toBe(
      'https://grafana.example.com',
    );

    const targets = buildObservabilityTargets(preferences, context);

    expect(targets[0].exploreUrl).toBe('https://grafana.example.com/explore');
    expect(targets[1].exploreUrl).toBe('https://jaeger.example.com/search');
    expect(targets[2].status).toBe('missing');
  });

  it('includes selected context summary in target metadata', () => {
    const targets = buildObservabilityTargets(preferences, context);

    expect(targets[0].contextSummary).toContain('workflow=direct');
    expect(targets[0].contextSummary).toContain('actorId=actor-1');
    expect(targets[0].contextSummary).toContain('commandId=cmd-1');
  });
});
