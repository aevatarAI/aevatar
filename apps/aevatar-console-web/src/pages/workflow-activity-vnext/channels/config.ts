const DEFAULT_ONBOARDING_URL =
  'https://aevatar-console-backend-api.aevatar.ai/channels';

export function getChannelOnboardingUrl(): string | null {
  const configured = process.env.AEVATAR_CHANNELS_ONBOARDING_URL;
  try {
    const url = new URL(
      configured === undefined ? DEFAULT_ONBOARDING_URL : configured,
    );
    return /^https?:$/.test(url.protocol) && !url.username && !url.password
      ? url.href
      : null;
  } catch {
    return null;
  }
}
