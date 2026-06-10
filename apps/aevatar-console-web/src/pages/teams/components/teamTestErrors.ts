export type TeamTestErrorKind =
  | "backend_unsupported"
  | "entry_missing"
  | "entry_not_ready"
  | "entry_not_found"
  | "entry_mismatch"
  | "entry_syncing"
  | "team_not_found"
  | "team_archived"
  | "permission_denied"
  | "invalid_entry"
  | "conflict"
  | "aborted"
  | "network"
  | "unknown";

export type TeamTestErrorDescription = {
  readonly action?: "retry";
  readonly actionLabel?: string;
  readonly description: string;
  readonly kind: TeamTestErrorKind;
  readonly title: string;
};

export type TeamTestErrorMessageFormatter = (
  id: string,
  values?: Record<string, string | number>,
) => string;

function readErrorStatus(error: unknown): number | undefined {
  if (!error || typeof error !== "object") {
    return undefined;
  }

  const status = (error as { status?: unknown }).status;
  return typeof status === "number" && Number.isFinite(status)
    ? status
    : undefined;
}

function readErrorCode(error: unknown): string {
  if (!error || typeof error !== "object") {
    return "";
  }

  const code = (error as { code?: unknown }).code;
  return typeof code === "string" ? code.trim().toUpperCase() : "";
}

function readErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error || "").trim();
}

export function isAbortLikeError(error: unknown): boolean {
  return (
    (typeof DOMException !== "undefined" &&
      error instanceof DOMException &&
      error.name === "AbortError")
  ) || readErrorMessage(error).toLowerCase().includes("abort");
}

export function describeTeamTestError(
  error: unknown,
  fallback: string,
  formatMessage: TeamTestErrorMessageFormatter,
): TeamTestErrorDescription {
  if (isAbortLikeError(error)) {
    return {
      description: formatMessage("teams.detail.test.errors.aborted.description"),
      kind: "aborted",
      title: formatMessage("teams.detail.test.errors.aborted.title"),
    };
  }

  const status = readErrorStatus(error);
  const code = readErrorCode(error);
  const message = readErrorMessage(error) || fallback;
  const normalized = `${code} ${message}`.toUpperCase();

  if (normalized.includes("TEAM_NOT_FOUND")) {
    return {
      description: formatMessage("teams.detail.test.errors.teamNotFound.description"),
      kind: "team_not_found",
      title: formatMessage("teams.detail.test.errors.teamNotFound.title"),
    };
  }

  if (normalized.includes("STUDIO_TEAM_NOT_FOUND")) {
    return {
      description: formatMessage("teams.detail.test.errors.teamNotFound.description"),
      kind: "team_not_found",
      title: formatMessage("teams.detail.test.errors.teamNotFound.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_CONFIGURED")) {
    return {
      description: formatMessage("teams.detail.test.errors.entryMissing.description"),
      kind: "entry_missing",
      title: formatMessage("teams.detail.test.errors.entryMissing.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_READY")) {
    const artifactMissing = normalized.includes("PREPARED_ARTIFACT_MISSING");
    return {
      description: formatMessage(
        artifactMissing
          ? "teams.detail.test.errors.entryArtifactMissing.description"
          : "teams.detail.test.errors.entryNotReady.description",
      ),
      kind: "entry_not_ready",
      title: formatMessage("teams.detail.test.errors.entryNotReady.title"),
    };
  }

  if (
    normalized.includes("TEAM_ENTRY_MEMBER_NOT_FOUND") ||
    normalized.includes("STUDIO_MEMBER_NOT_FOUND")
  ) {
    return {
      description: formatMessage("teams.detail.test.errors.entryNotFound.description"),
      kind: "entry_not_found",
      title: formatMessage("teams.detail.test.errors.entryNotFound.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_MISMATCH")) {
    return {
      description: formatMessage("teams.detail.test.errors.entryMismatch.description"),
      kind: "entry_mismatch",
      title: formatMessage("teams.detail.test.errors.entryMismatch.title"),
    };
  }

  if (normalized.includes("TEAM_ARCHIVED")) {
    return {
      description: formatMessage("teams.detail.test.errors.teamArchived.description"),
      kind: "team_archived",
      title: formatMessage("teams.detail.test.errors.teamArchived.title"),
    };
  }

  if (status === 404 || status === 405) {
    return {
      action: "retry",
      actionLabel: formatMessage("teams.detail.test.actions.retry"),
      description: formatMessage("teams.detail.test.errors.backendUnsupported.description"),
      kind: "backend_unsupported",
      title: formatMessage("teams.detail.test.errors.backendUnsupported.title"),
    };
  }

  if (status === 403) {
    return {
      description: formatMessage("teams.detail.test.errors.permissionDenied.description"),
      kind: "permission_denied",
      title: formatMessage("teams.detail.test.errors.permissionDenied.title"),
    };
  }

  if (status === 400) {
    return {
      description: message,
      kind: "invalid_entry",
      title: formatMessage("teams.detail.test.errors.invalidEntry.title"),
    };
  }

  if (status === 409) {
    return {
      action: "retry",
      actionLabel: formatMessage("teams.detail.test.actions.retry"),
      description: message,
      kind: "conflict",
      title: formatMessage("teams.detail.test.errors.conflict.title"),
    };
  }

  if (
    message.toLowerCase().includes("failed to fetch") ||
    message.toLowerCase().includes("network")
  ) {
    return {
      action: "retry",
      actionLabel: formatMessage("teams.detail.test.actions.retry"),
      description: formatMessage("teams.detail.test.errors.network.description"),
      kind: "network",
      title: formatMessage("teams.detail.test.errors.network.title"),
    };
  }

  return {
    action: "retry",
    actionLabel: formatMessage("teams.detail.test.actions.retry"),
    description: message,
    kind: "unknown",
    title: fallback,
  };
}
