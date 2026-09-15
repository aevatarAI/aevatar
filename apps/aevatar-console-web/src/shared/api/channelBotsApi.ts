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
  const bots = expectArray(body.bots, 'Channel bots', (value) => {
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
  });
  if (new Set(bots.map((bot) => bot.id)).size !== bots.length)
    throw new Error('Ambiguous channel bot identity.');
  return bots;
}
