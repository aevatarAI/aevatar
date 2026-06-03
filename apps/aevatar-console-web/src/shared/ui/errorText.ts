import { t } from "@/shared/i18n/messages";
function normalizeWhitespace(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

function normalizeUserFacingError(message: string): string {
  const normalized = normalizeWhitespace(message);
  if (!normalized) {
    return "";
  }

  const lower = normalized.toLowerCase();
  const isProxyFailure =
    lower.includes("error occurred while trying to proxy") ||
    lower.includes("failed to fetch") ||
    lower.includes("network error");

  if (!isProxyFailure) {
    return normalized;
  }

  if (lower.includes("/api/auth/") || lower.includes("auth")) {
    return t("shared.ui.errortext.the.login.status.is", "The login status is temporarily unavailable, please refresh and try again.");
  }

  if (lower.includes("workspace")) {
    return t("shared.ui.errortext.workspace.settings.are.temporarily", "Workspace settings are temporarily unavailable, please try again later.");
  }

  if (lower.includes("app context")) {
    return t("shared.ui.errortext.the.current.context.is", "The current context is temporarily unavailable, please try again later.");
  }

  return t("shared.ui.errortext.the.current.service.is", "The current service is temporarily unavailable, please try again later.");
}

export function describeError(
  error: unknown,
  fallback = t("shared.ui.errortext.the.current.service.is.2", "The current service is temporarily unavailable, please try again later.")
): string {
  if (error instanceof Error) {
    const message = normalizeUserFacingError(error.message || error.name || "");
    return message || fallback;
  }

  if (error && typeof error === "object" && !Array.isArray(error)) {
    const record = error as { message?: unknown };
    if (typeof record.message === "string") {
      const message = normalizeUserFacingError(record.message);
      if (message) {
        return message;
      }
    }
  }

  const text = normalizeUserFacingError(String(error ?? ""));
  return text || fallback;
}
