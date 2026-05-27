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
  fallback = "团队测试失败。"
): TeamTestErrorDescription {
  if (isAbortLikeError(error)) {
    return {
      description: "本次测试已停止，当前 transcript 会保留在页面内。",
      kind: "aborted",
      title: "测试已停止",
    };
  }

  const status = readErrorStatus(error);
  const code = readErrorCode(error);
  const message = readErrorMessage(error) || fallback;
  const normalized = `${code} ${message}`.toUpperCase();

  if (normalized.includes("TEAM_NOT_FOUND")) {
    return {
      description: "这支团队在当前工作区中不可见，请返回团队列表重新选择。",
      kind: "team_not_found",
      title: "团队不存在",
    };
  }

  if (normalized.includes("STUDIO_TEAM_NOT_FOUND")) {
    return {
      description: "这支团队在当前工作区中不可见，请返回团队列表重新选择。",
      kind: "team_not_found",
      title: "团队不存在",
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_CONFIGURED")) {
    return {
      description: "这支团队还没有入口成员，请先选择一个已绑定的成员作为入口。",
      kind: "entry_missing",
      title: "未设置入口成员",
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_NOT_READY")) {
    return {
      description: "入口成员还没有完成构建和绑定，暂时不能作为团队测试的运行入口。",
      kind: "entry_not_ready",
      title: "入口成员尚未就绪",
    };
  }

  if (
    normalized.includes("TEAM_ENTRY_MEMBER_NOT_FOUND") ||
    normalized.includes("STUDIO_MEMBER_NOT_FOUND")
  ) {
    return {
      description: "当前入口成员不在这支团队的成员清单中，请重新选择入口成员。",
      kind: "entry_not_found",
      title: "入口成员不可见",
    };
  }

  if (normalized.includes("TEAM_ENTRY_MEMBER_MISMATCH")) {
    return {
      description: "入口成员不属于当前团队，请从当前团队成员中重新选择。",
      kind: "entry_mismatch",
      title: "入口成员不匹配",
    };
  }

  if (normalized.includes("TEAM_ARCHIVED")) {
    return {
      description: "归档后的团队不能继续发起测试。",
      kind: "team_archived",
      title: "团队已归档",
    };
  }

  if (status === 404 || status === 405) {
    return {
      actionLabel: "重试",
      description:
        "当前后端还没有部署团队入口成员或团队调用接口。前端会保留入口配置和测试草稿，等后端支持后可直接重试。",
      kind: "backend_unsupported",
      title: "后端暂不支持团队测试",
    };
  }

  if (status === 403) {
    return {
      description: "当前账号没有修改或测试这支团队的权限。",
      kind: "permission_denied",
      title: "权限不足",
    };
  }

  if (status === 400) {
    return {
      description:
        "当前入口成员的绑定产物不可用。请回到工作室重新构建和绑定该成员，然后再测试团队。",
      kind: "invalid_entry",
      title: "入口成员无效",
    };
  }

  if (status === 409) {
    return {
      actionLabel: "重试",
      description: message,
      kind: "conflict",
      title: "团队状态已变化",
    };
  }

  if (
    message.toLowerCase().includes("failed to fetch") ||
    message.toLowerCase().includes("network")
  ) {
    return {
      actionLabel: "重试",
      description: "网络请求中断，请检查登录状态或稍后重试。",
      kind: "network",
      title: "网络请求失败",
    };
  }

  return {
    actionLabel: "重试",
    description: message,
    kind: "unknown",
    title: fallback,
  };
}
