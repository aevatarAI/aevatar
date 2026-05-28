/**
 * Shared CQRS-aligned UI state vocabulary.
 *
 * Derived from docs/canon/frontend-design.md section 4.1.
 * Every status model in the frontend MUST use a subset of these values;
 * ad-hoc aliases like "success", "error", "stopped" are forbidden.
 */
export type CqrsStatus =
  | "idle"
  | "accepted"
  | "running"
  | "streaming"
  | "paused"
  | "observed"
  | "completed"
  | "still-processing"
  | "failed";
