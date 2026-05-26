function parseBooleanFlag(value: string | undefined, fallback: boolean): boolean {
  const normalized = value?.trim().toLowerCase();
  if (!normalized) {
    return fallback;
  }

  if (["1", "true", "yes", "on", "enabled"].includes(normalized)) {
    return true;
  }

  if (["0", "false", "no", "off", "disabled"].includes(normalized)) {
    return false;
  }

  return fallback;
}

const TEAM_FIRST_ENABLED = parseBooleanFlag(
  process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED,
  true,
);

export function isTeamFirstEnabled(): boolean {
  return TEAM_FIRST_ENABLED;
}

export const CONSOLE_FEATURES = {
  teamFirstEnabled: TEAM_FIRST_ENABLED,
} as const;
