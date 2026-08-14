function parseBooleanFlag(
  value: string | undefined,
  fallback: boolean,
): boolean {
  const normalized = value?.trim().toLowerCase();
  if (!normalized) {
    return fallback;
  }

  if (['1', 'true', 'yes', 'on', 'enabled'].includes(normalized)) {
    return true;
  }

  if (['0', 'false', 'no', 'off', 'disabled'].includes(normalized)) {
    return false;
  }

  return fallback;
}

const TEAM_FIRST_ENABLED = parseBooleanFlag(
  process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED,
  true,
);

const NYXID_CHAT_WIRE_INSPECTOR_ENABLED = parseBooleanFlag(
  process.env.AEVATAR_CONSOLE_NYXID_CHAT_WIRE_INSPECTOR_ENABLED,
  false,
);

export function isTeamFirstEnabled(): boolean {
  return TEAM_FIRST_ENABLED;
}

export function isNyxIdChatWireInspectorEnabled(): boolean {
  return NYXID_CHAT_WIRE_INSPECTOR_ENABLED;
}

export const CONSOLE_FEATURES = {
  nyxIdChatWireInspectorEnabled: NYXID_CHAT_WIRE_INSPECTOR_ENABLED,
  teamFirstEnabled: TEAM_FIRST_ENABLED,
} as const;
