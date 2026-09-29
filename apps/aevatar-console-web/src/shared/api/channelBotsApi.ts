import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { authFetch } from '@/shared/auth/fetch';
import { ChannelApiError } from './channelsApi';
import { expectArray, expectRecord, readString } from './http/decoders';

export interface ChannelBotIdentity {
  readonly id: string;
  readonly platform: string;
  readonly label: string | null;
}

export async function listChannelBotIdentities(
  signal?: AbortSignal,
): Promise<ChannelBotIdentity[]> {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  // Omitting org_id reads only the authenticated owner's personal bots.
  const response = await authFetch(`${config.baseUrl}/api/v1/channel-bots`, {
    signal,
    credentials: 'omit',
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  const body = expectRecord(await response.json(), 'Channel bots');
  const bots = expectArray(body.bots, 'Channel bots', decodeBotIdentity);
  if (new Set(bots.map((bot) => bot.id)).size !== bots.length)
    throw new Error('Ambiguous channel bot identity.');
  return bots;
}

function decodeBotIdentity(value: unknown): ChannelBotIdentity {
  const bot = expectRecord(value, 'Channel bot');
  const id = readString(bot, 'id', 'Channel bot ID');
  const platform = readString(bot, 'platform', 'Channel bot platform');
  if (!id.trim() || !platform.trim())
    throw new Error('Missing channel bot identity.');
  // Keep only safe identity fields, never the full upstream object.
  return {
    id,
    platform,
    label: readString(bot, 'label', 'Channel bot label').trim() || null,
  };
}

export function isValidChannelBotLabel(label: string): boolean {
  const value = label.trim();
  // NyxID validates the trimmed UTF-8 byte length, not JavaScript code units.
  return Boolean(value) && new TextEncoder().encode(value).length <= 128;
}

export async function updateChannelBotLabel(
  bot: ChannelBotIdentity,
  label: string,
): Promise<ChannelBotIdentity> {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  const value = label.trim();
  if (!bot.id.trim() || !isValidChannelBotLabel(value))
    throw new Error('Invalid channel bot label.');
  const response = await authFetch(
    `${config.baseUrl}/api/v1/channel-bots/${encodeURIComponent(bot.id)}`,
    {
      method: 'PATCH',
      credentials: 'omit',
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ label: value }),
    },
  );
  if (!response.ok) throw new ChannelApiError(response.status);
  const updated = decodeBotIdentity(await response.json());
  if (
    updated.id !== bot.id ||
    updated.platform !== bot.platform ||
    updated.label !== value
  )
    throw new Error('Channel label update was not confirmed.');
  return updated;
}
