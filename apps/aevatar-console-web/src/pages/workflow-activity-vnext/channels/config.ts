const DEFAULT_WEBHOOK_BASE_URL =
  'https://aevatar-console-backend-api.aevatar.ai';

export function getChannelWebhookBaseUrl(): string | null {
  const configured = process.env.AEVATAR_CHANNEL_WEBHOOK_BASE_URL;
  try {
    const url = new URL(
      configured === undefined ? DEFAULT_WEBHOOK_BASE_URL : configured,
    );
    return url.protocol === 'https:' &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash
      ? url.href.replace(/\/+$/, '')
      : null;
  } catch {
    return null;
  }
}
