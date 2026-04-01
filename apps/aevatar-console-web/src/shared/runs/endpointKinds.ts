export type RunEndpointKind = "chat" | "command";

export function normalizeRunEndpointKind(
  endpointKind?: string | null,
  endpointId?: string | null
): RunEndpointKind {
  const normalizedEndpointKind = endpointKind?.trim().toLowerCase() ?? "";
  if (normalizedEndpointKind === "chat") {
    return "chat";
  }

  if (normalizedEndpointKind === "command") {
    return "command";
  }

  return endpointId?.trim() === "chat" ? "chat" : "command";
}

export function resolveRunEndpointId(
  endpointKind?: string | null,
  endpointId?: string | null
): string {
  const normalizedEndpointId = endpointId?.trim() ?? "";
  if (normalizedEndpointId) {
    return normalizedEndpointId;
  }

  return normalizeRunEndpointKind(endpointKind, endpointId) === "chat"
    ? "chat"
    : "";
}
