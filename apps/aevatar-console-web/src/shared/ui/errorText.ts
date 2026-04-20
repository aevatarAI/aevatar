function normalizeWhitespace(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

function normalizeUserFacingError(message: string): string {
  const normalized = normalizeWhitespace(message);
  if (!normalized) {
    return "";
  }

  const lower = normalized.toLowerCase();
  const isUnsupportedModelRoute =
    lower.includes("invalid_request_error") &&
    lower.includes("not supported when using codex with a chatgpt account");

  if (isUnsupportedModelRoute) {
    return "当前 AI 模型与路由配置不兼容，请切换到 Gateway 或受支持的模型后重试。";
  }

  const isProxyFailure =
    lower.includes("error occurred while trying to proxy") ||
    lower.includes("failed to fetch") ||
    lower.includes("network error");

  if (!isProxyFailure) {
    return normalized;
  }

  if (lower.includes("/api/auth/") || lower.includes("auth")) {
    return "登录状态暂时不可用，请刷新后重试。";
  }

  if (lower.includes("workspace")) {
    return "工作区设置暂时不可用，请稍后再试。";
  }

  if (lower.includes("app context")) {
    return "当前上下文暂时不可用，请稍后再试。";
  }

  return "当前服务暂时不可用，请稍后再试。";
}

export function describeError(
  error: unknown,
  fallback = "当前服务暂时不可用，请稍后再试。"
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
