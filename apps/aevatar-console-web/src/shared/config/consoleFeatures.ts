function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function readBooleanEnv(value: string | null | undefined): boolean {
  const normalized = trimOptional(value).toLowerCase();
  return (
    normalized === "1" ||
    normalized === "true" ||
    normalized === "yes" ||
    normalized === "on"
  );
}

export function isTeamFirstEnabled(): boolean {
  return readBooleanEnv(process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED);
}

export const CONSOLE_FEATURES = {
  teamFirstEnabled: isTeamFirstEnabled(),
} as const;
