import type {
  WorkflowActivityDiagnostic,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
  WorkflowActivityUsageTotals,
} from '@/shared/models/workflowActivity';

export type RunStepMetrics = {
  readonly attempted: number;
  readonly failed: number;
  readonly skipped: null;
  readonly succeeded: number;
  readonly waiting: number;
};

export type RunFailurePresentation = {
  readonly evidence: readonly string[];
  readonly primaryCause: string;
};

export type RunUsagePresentation = {
  readonly completionTokens: number | null;
  readonly cost: number | null;
  readonly currency: null;
  readonly promptTokens: number | null;
  readonly state: 'not_reported' | 'reported';
  readonly toolCalls: number | null;
  readonly totalTokens: number | null;
};

function normalizeEvidence(value: string): string {
  return value.trim().replace(/\s+/g, ' ').toLowerCase();
}

function humanize(value: string): string {
  const words = value
    .trim()
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .toLowerCase();
  if (!words) return '';
  return words
    .replace(/^llm\b/, 'LLM')
    .replace(/^ai\b/, 'AI')
    .replace(/^./, (character) => character.toUpperCase());
}

export function buildStepMetrics(
  steps: readonly WorkflowActivityStep[],
): RunStepMetrics {
  return {
    attempted: steps.length,
    failed: steps.filter((step) => step.success === false).length,
    skipped: null,
    succeeded: steps.filter((step) => step.success === true).length,
    waiting: steps.filter((step) => step.success === null).length,
  };
}

export function buildFailurePresentation(input: {
  readonly diagnostics: readonly WorkflowActivityDiagnostic[];
  readonly finalError: string;
  readonly steps: readonly WorkflowActivityStep[];
}): RunFailurePresentation {
  const preferredDiagnostics = [...input.diagnostics].sort((left, right) => {
    const leftError = left.severity.trim().toLowerCase() === 'error' ? 0 : 1;
    const rightError = right.severity.trim().toLowerCase() === 'error' ? 0 : 1;
    return leftError - rightError;
  });
  const candidates = [
    ...preferredDiagnostics.map((item) => item.message),
    input.finalError,
    ...input.steps.map((item) => item.error),
  ];
  const seen = new Set<string>();
  const evidence = candidates.flatMap((candidate) => {
    const value = candidate.trim();
    const key = normalizeEvidence(value);
    if (!key || seen.has(key)) return [];
    seen.add(key);
    return [value];
  });
  return {
    evidence,
    primaryCause: evidence[0] ?? '',
  };
}

export function buildUsagePresentation(
  totals: WorkflowActivityUsageTotals,
  steps: readonly WorkflowActivityStep[],
  timeline: readonly WorkflowActivityTimelineEvent[],
): RunUsagePresentation {
  const stepTotals = steps.reduce(
    (result, step) => ({
      completionTokens: result.completionTokens + step.usage.completionTokens,
      cost: result.cost + step.usage.cost,
      promptTokens: result.promptTokens + step.usage.promptTokens,
      totalTokens: result.totalTokens + step.usage.totalTokens,
    }),
    { completionTokens: 0, cost: 0, promptTokens: 0, totalTokens: 0 },
  );
  const totalsReported = Object.values(totals).some((value) => value !== 0);
  const stepsReported = Object.values(stepTotals).some((value) => value !== 0);
  const reported = totalsReported || stepsReported;
  const resolved = totalsReported ? totals : stepTotals;
  const toolCalls = timeline.filter((event) => event.toolCall !== null).length;
  return {
    completionTokens: reported ? resolved.completionTokens : null,
    cost: reported ? resolved.cost : null,
    currency: null,
    promptTokens: reported ? resolved.promptTokens : null,
    state: reported ? 'reported' : 'not_reported',
    toolCalls: toolCalls > 0 ? toolCalls : null,
    totalTokens: reported ? resolved.totalTokens : null,
  };
}

export function getStepDisplayName(step: WorkflowActivityStep): string {
  const stepType = humanize(step.stepType) || 'Workflow step';
  const targetRole = humanize(step.targetRole);
  return targetRole ? `${targetRole} · ${stepType}` : stepType;
}

export function getTimelineEventLabel(event: {
  readonly kind: string;
  readonly stage: string;
}): string {
  switch (event.kind.trim().toLowerCase()) {
    case 'runstarted':
      return 'Run started';
    case 'runcompleted':
      return 'Run completed';
    case 'runerror':
      return 'Run failed';
    case 'runstopped':
      return 'Run stopped';
    default:
      break;
  }
  switch (event.stage.trim().toLowerCase()) {
    case 'role.reply':
      return 'Step produced a response';
    case 'tool.call':
      return 'Tool started';
    case 'tool.result':
      return 'Tool finished';
    default:
      return 'Run updated';
  }
}
