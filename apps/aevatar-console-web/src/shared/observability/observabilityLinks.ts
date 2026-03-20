import type { ConsolePreferences } from '@/shared/preferences/consolePreferences';

export type ObservabilityContext = {
  workflow: string;
  actorId: string;
  commandId: string;
  runId: string;
  stepId: string;
};

export type ObservabilityTarget = {
  id: 'grafana' | 'jaeger' | 'loki';
  label: string;
  status: 'configured' | 'missing';
  homeUrl: string;
  exploreUrl: string;
  description: string;
  contextSummary: string;
};

export function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/, '');
}

function buildContextSummary(context: ObservabilityContext): string {
  const parts = [
    context.workflow ? `workflow=${context.workflow}` : '',
    context.actorId ? `actorId=${context.actorId}` : '',
    context.commandId ? `commandId=${context.commandId}` : '',
    context.runId ? `runId=${context.runId}` : '',
    context.stepId ? `stepId=${context.stepId}` : '',
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' | ') : 'No context selected';
}

export function buildObservabilityTargets(
  preferences: ConsolePreferences,
  context: ObservabilityContext,
): ObservabilityTarget[] {
  const grafanaBaseUrl = normalizeBaseUrl(preferences.grafanaBaseUrl);
  const jaegerBaseUrl = normalizeBaseUrl(preferences.jaegerBaseUrl);
  const lokiBaseUrl = normalizeBaseUrl(preferences.lokiBaseUrl);
  const contextSummary = buildContextSummary(context);

  return [
    {
      id: 'grafana',
      label: 'Grafana',
      status: grafanaBaseUrl ? 'configured' : 'missing',
      homeUrl: grafanaBaseUrl,
      exploreUrl: grafanaBaseUrl ? `${grafanaBaseUrl}/explore` : '',
      description: 'Dashboards and Explore entrypoint for traces, logs, and linked views.',
      contextSummary,
    },
    {
      id: 'jaeger',
      label: 'Jaeger',
      status: jaegerBaseUrl ? 'configured' : 'missing',
      homeUrl: jaegerBaseUrl,
      exploreUrl: jaegerBaseUrl ? `${jaegerBaseUrl}/search` : '',
      description: 'Trace search and timeline inspection for distributed workflow execution.',
      contextSummary,
    },
    {
      id: 'loki',
      label: 'Loki',
      status: lokiBaseUrl ? 'configured' : 'missing',
      homeUrl: lokiBaseUrl,
      exploreUrl: lokiBaseUrl,
      description: 'Log aggregation entrypoint for actor, workflow, and command correlations.',
      contextSummary,
    },
  ];
}
