/**
 * Shared CQRS UI state model.
 *
 * Maps the full lifecycle of a CQRS command/observation/readmodel
 * into distinct UI states per docs/canon/frontend-design.md section 4.1.
 *
 * | State           | Source                    | Meaning                                                       |
 * |-----------------|---------------------------|---------------------------------------------------------------|
 * | idle            | Local UI                  | No command sent yet.                                          |
 * | accepted        | Command receipt           | Backend accepted the command and returned a stable commandId. |
 * | running         | Observation               | Actor/workflow is executing.                                  |
 * | streaming       | Observation (sub-state)   | AGUI/SSE/WS is pushing tokens or step events.                |
 * | paused          | Observation / ReadModel   | Waiting for human input, approval, or signal.                 |
 * | observed        | ReadModel                 | Query result materialized at a known stateVersion.            |
 * | completed       | Public business status    | Run/message/service action finished successfully.             |
 * | stillProcessing | Local UI + timeout policy | No new observation, but failure not confirmed.                |
 * | failed          | Public error status       | API, observation, or readModel returned a failure.            |
 * | cancelled       | Local UI                  | Operator aborted the run.                                     |
 */

/** Full CQRS UI state union. */
export type CqrsStatus =
  | 'idle'
  | 'accepted'
  | 'running'
  | 'streaming'
  | 'paused'
  | 'observed'
  | 'completed'
  | 'stillProcessing'
  | 'failed'
  | 'cancelled'
  // Transitional: legacy values still used by unmigrated components.
  // Prefer 'completed' over 'success' and 'failed' over 'error' in new code.
  | 'success'
  | 'error';

/**
 * Legacy 4-state model used by many existing components.
 * Kept for backward compatibility during migration; prefer {@link CqrsStatus}.
 */
export type LegacyRunStatus = 'idle' | 'running' | 'success' | 'error';

/**
 * Legacy 5-state model used by invoke panels (includes 'cancelled').
 * Kept for backward compatibility during migration; prefer {@link CqrsStatus}.
 */
export type LegacyInvokeStatus =
  | 'idle'
  | 'running'
  | 'success'
  | 'error'
  | 'cancelled';

/**
 * Legacy observe-session status model (no 'idle', no 'cancelled').
 * Kept for backward compatibility during migration; prefer {@link CqrsStatus}.
 */
export type LegacyObserveStatus = 'running' | 'success' | 'error';

/**
 * Legacy history entry status model (no 'idle').
 * Kept for backward compatibility during migration; prefer {@link CqrsStatus}.
 */
export type LegacyHistoryStatus =
  | 'running'
  | 'success'
  | 'error'
  | 'cancelled';

/**
 * Union of all legacy status string literals that this module can map.
 */
export type AnyLegacyStatus =
  | LegacyRunStatus
  | LegacyInvokeStatus
  | LegacyObserveStatus
  | LegacyHistoryStatus;

/**
 * Map a legacy status value to the canonical {@link CqrsStatus}.
 *
 * Mapping rules:
 * - `'success'` -> `'completed'` (was conflating receipt, observation, and materialization)
 * - `'error'`   -> `'failed'`
 * - Other values pass through unchanged.
 */
export function toCqrsStatus(status: AnyLegacyStatus | CqrsStatus): CqrsStatus {
  switch (status) {
    case 'success':
      return 'completed';
    case 'error':
      return 'failed';
    default:
      return status;
  }
}

/**
 * Map a {@link CqrsStatus} back to the legacy 4-state model for components
 * that have not yet been migrated to the full state set.
 *
 * Lossy: `'accepted'`, `'streaming'`, `'paused'`, `'observed'`,
 * `'stillProcessing'`, `'cancelled'` all collapse.
 */
export function toLegacyRunStatus(status: CqrsStatus): LegacyRunStatus {
  switch (status) {
    case 'idle':
      return 'idle';
    case 'running':
    case 'accepted':
    case 'streaming':
    case 'paused':
    case 'stillProcessing':
      return 'running';
    case 'completed':
    case 'observed':
      return 'success';
    case 'failed':
    case 'cancelled':
      return 'error';
    default:
      return 'idle';
  }
}

/**
 * Map a {@link CqrsStatus} back to the legacy 5-state invoke model.
 *
 * Lossy: `'streaming'`, `'paused'`, `'observed'`, `'stillProcessing'` collapse.
 */
export function toLegacyInvokeStatus(
  status: CqrsStatus,
): LegacyInvokeStatus {
  switch (status) {
    case 'idle':
      return 'idle';
    case 'running':
    case 'accepted':
    case 'streaming':
    case 'paused':
    case 'stillProcessing':
      return 'running';
    case 'completed':
    case 'observed':
      return 'success';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'idle';
  }
}

/**
 * Returns true when the status represents a terminal (no further transitions expected) state.
 */
export function isCqrsTerminal(status: CqrsStatus): boolean {
  return (
    status === 'completed' ||
    status === 'success' ||
    status === 'failed' ||
    status === 'error' ||
    status === 'cancelled' ||
    status === 'observed'
  );
}

/**
 * Returns true when the status indicates active progress (command accepted, running, or streaming).
 */
export function isCqrsActive(status: CqrsStatus): boolean {
  return (
    status === 'accepted' ||
    status === 'running' ||
    status === 'streaming' ||
    status === 'stillProcessing'
  );
}
