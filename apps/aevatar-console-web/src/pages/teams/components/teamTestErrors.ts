import { translate } from "@/shared/i18n/localization";

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
  readonly actionLabel?: string;
  readonly description: string;
  readonly kind: TeamTestErrorKind;
  readonly title: string;
};

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
  fallback = translate("team.test.error.unknown.title"),
): TeamTestErrorDescription {
  if (isAbortLikeError(error)) {
    return {
      description: translate("team.test.error.aborted.description"),
      kind: "aborted",
      title: translate("team.test.error.aborted.title"),
    };
  }

  const status = readErrorStatus(error);
  const code = readErrorCode(error);
  const message = readErrorMessage(error) || fallback;
  const normalized = `${code} ${message}`.toUpperCase();

  if (normalized.includes("TEAM_NOT_FOUND")) {
    return {
      description: translate("team.test.error.teamNotFound.description"),
      kind: "team_not_found",
      title: translate("team.test.error.teamNotFound.title"),
    };
  }

  if (normalized.includes("STUDIO_TEAM_NOT_FOUND")) {
    return {
      description: translate("team.test.error.teamNotFound.description"),
      kind: "team_not_found",
      title: translate("team.test.error.teamNotFound.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_CONFIGURED")) {
    return {
      description: translate("team.test.error.entryMissing.description"),
      kind: "entry_missing",
      title: translate("team.test.error.entryMissing.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_READY")) {
    return {
      description: translate("team.test.error.entryNotReady.description"),
      kind: "entry_not_ready",
      title: translate("team.test.error.entryNotReady.title"),
    };
  }

  if (
    normalized.includes("TEAM_ENTRY_MEMBER_NOT_FOUND") ||
    normalized.includes("STUDIO_MEMBER_NOT_FOUND")
  ) {
    return {
      description: translate("team.test.error.entryNotFound.description"),
      kind: "entry_not_found",
      title: translate("team.test.error.entryNotFound.title"),
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_MISMATCH")) {
    return {
      description: translate("team.test.error.entryMismatch.description"),
      kind: "entry_mismatch",
      title: translate("team.test.error.entryMismatch.title"),
    };
  }

  if (normalized.includes("TEAM_ARCHIVED")) {
    return {
      description: translate("team.test.error.teamArchived.description"),
      kind: "team_archived",
      title: translate("team.test.error.teamArchived.title"),
    };
  }

  if (status === 404 || status === 405) {
    return {
      actionLabel: translate("common.retry"),
      description:
        translate("team.test.error.backendUnsupported.description"),
      kind: "backend_unsupported",
      title: translate("team.test.error.backendUnsupported.title"),
    };
  }

  if (status === 403) {
    return {
      description: translate("team.test.error.permissionDenied.description"),
      kind: "permission_denied",
      title: translate("team.test.error.permissionDenied.title"),
    };
  }

  if (status === 400) {
    return {
      description: message,
      kind: "invalid_entry",
      title: translate("team.test.error.invalidEntry.title"),
    };
  }

  if (status === 409) {
    return {
      actionLabel: translate("common.retry"),
      description: message,
      kind: "conflict",
      title: translate("team.test.error.conflict.title"),
    };
  }

  if (
    message.toLowerCase().includes("failed to fetch") ||
    message.toLowerCase().includes("network")
  ) {
    return {
      actionLabel: translate("common.retry"),
      description: translate("team.test.error.network.description"),
      kind: "network",
      title: translate("team.test.error.network.title"),
    };
  }

  return {
    actionLabel: translate("common.retry"),
    description: message,
    kind: "unknown",
    title: fallback,
  };
}
