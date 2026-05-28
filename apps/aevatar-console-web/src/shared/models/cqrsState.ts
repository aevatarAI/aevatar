/**
 * CQRS-aligned UI state vocabulary.
 *
 * These types enforce the canonical CQRS status names from
 * docs/canon/frontend-design.md §4.1 across all feature modules.
 *
 * Run statuses represent the lifecycle of a workflow run.
 * Node statuses represent the lifecycle of an individual step or actor node.
 */

export const cqrsRunStatuses = [
  'localDraft',
  'accepted',
  'running',
  'streaming',
  'paused',
  'observed',
  'completed',
  'stillProcessing',
  'failed',
] as const;

export type CqrsRunStatus = (typeof cqrsRunStatuses)[number];

export const cqrsNodeStatuses = [
  'idle',
  'running',
  'paused',
  'completed',
  'failed',
] as const;

export type CqrsNodeStatus = (typeof cqrsNodeStatuses)[number];
