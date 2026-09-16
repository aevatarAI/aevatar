import { focusManager, onlineManager } from '@tanstack/react-query';
import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { history } from '@/shared/navigation/history';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import ChannelDetailsPage from './ChannelDetailsPage';
import ChannelsPage from './ChannelsPage';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));
const mockToast = {
  success: jest.fn(),
  warning: jest.fn(),
  error: jest.fn(),
  info: jest.fn(),
};
jest.mock('@/shared/ui/ConsoleToast', () => ({
  ...jest.requireActual('@/shared/ui/ConsoleToast'),
  useConsoleToast: () => mockToast,
}));

const fetchMock = jest.mocked(authFetch);
const registration = {
  id: 'registration:alpha/one',
  platform: 'telegram',
  scope_id: 'scope-alpha',
  owned: true,
  nyx_channel_bot_id: 'bot-alpha',
  nyx_provider_slug: 'telegram-provider',
  nyx_agent_api_key_id: 'key-alpha',
  default_skill: { name: 'review.skill', version: '2.4' },
  workflow_result_delivery_status: 'enabled',
};
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;
const statusPath =
  '/api/channels/registrations/registration%3Aalpha%2Fone/status';
const deletePath = '/api/channels/registrations/registration%3Aalpha%2Fone';
const botsPath = 'https://nyx.example.test/api/v1/channel-bots';
const bot = { id: 'bot-alpha', platform: 'telegram', label: 'Team channel' };
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

beforeEach(() => {
  fetchMock.mockReset();
  Object.values(mockToast).forEach((mock) => {
    mock.mockReset();
  });
  jest.spyOn(history, 'push').mockImplementation(() => {});
  jest.spyOn(history, 'replace').mockImplementation(() => {});
  fetchMock.mockImplementation(async (input) => {
    if (input === botsPath) return response({ bots: [bot] });
    if (input === '/api/channels/registrations')
      return response([registration]);
    if (input === statusPath)
      return response({ registration_id: registration.id, status: 'active' });
    throw new Error(`Unexpected test request: ${String(input)}`);
  });
});

describe('Channel pages', () => {
  it('refreshes the list and statuses only when requested after the initial load', async () => {
    jest.useFakeTimers();
    try {
      const view = renderWithQueryClient(
        <ChannelsPage scopeId="scope-alpha" />,
      );
      await screen.findByText('Active');
      await screen.findByText('Team channel');
      expect(fetchMock.mock.calls.map(([input]) => input).sort()).toEqual(
        ['/api/channels/registrations', statusPath, botsPath].sort(),
      );
      await act(async () => {
        await jest.advanceTimersByTimeAsync(60_000);
        focusManager.setFocused(false);
        focusManager.setFocused(true);
        onlineManager.setOnline(false);
        onlineManager.setOnline(true);
        await jest.advanceTimersByTimeAsync(1);
      });
      expect(fetchMock).toHaveBeenCalledTimes(3);
      fireEvent.click(screen.getByRole('button', { name: 'Refresh channels' }));
      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(6));
      expect(
        fetchMock.mock.calls
          .slice(3)
          .map(([input]) => input)
          .sort(),
      ).toEqual(['/api/channels/registrations', statusPath, botsPath].sort());
      view.unmount();
    } finally {
      focusManager.setFocused(undefined);
      onlineManager.setOnline(true);
      jest.useRealTimers();
    }
  });

  it.each([
    200, 503,
  ])('keeps the table busy until all manual refresh reads settle when the list returns %s', async (listStatus) => {
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    await screen.findByText('Team channel');
    await screen.findByText('Active');
    const table = screen.getByRole('table', { name: /^Connected/ });
    const refreshButton = screen.getByRole('button', {
      name: 'Refresh channels',
    });
    const pendingList = deferred<Response>();
    const pendingStatus = deferred<Response>();
    const pendingNames = deferred<Response>();
    fetchMock.mockClear();
    fetchMock.mockImplementation(async (input) => {
      if (input === '/api/channels/registrations') return pendingList.promise;
      if (input === statusPath) return pendingStatus.promise;
      if (input === botsPath) return pendingNames.promise;
      throw new Error(`Unexpected test request: ${String(input)}`);
    });

    fireEvent.click(refreshButton);
    expect(refreshButton).toBeDisabled();
    expect(
      screen.getByRole('status', { name: 'Loading channels' }),
    ).toBeInTheDocument();
    expect(table.closest('[aria-busy]')).toHaveAttribute('aria-busy', 'true');
    expect(table).toHaveAttribute('inert');
    expect(within(table).getByText('Team channel')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Connect Telegram' }).closest('[inert]'),
    ).toBeNull();
    fireEvent.click(refreshButton);
    expect(fetchMock.mock.calls.map(([input]) => input).sort()).toEqual(
      ['/api/channels/registrations', statusPath, botsPath].sort(),
    );

    await act(async () => {
      pendingList.resolve(response([registration], listStatus));
    });
    expect(refreshButton).toBeDisabled();
    expect(table.closest('[aria-busy]')).toHaveAttribute('aria-busy', 'true');
    await act(async () => {
      pendingStatus.resolve(
        response({ registration_id: registration.id, status: 'active' }),
      );
    });
    expect(
      screen.getByRole('status', { name: 'Loading channels' }),
    ).toBeInTheDocument();
    expect(refreshButton).toBeDisabled();

    await act(async () => {
      pendingNames.resolve(
        response({ bots: [{ ...bot, label: 'Updated channel' }] }),
      );
    });
    await waitFor(() => expect(refreshButton).toBeEnabled());
    expect(screen.getByRole('table', { name: /^Connected/ })).toBe(table);
    expect(table.closest('[aria-busy]')).toHaveAttribute('aria-busy', 'false');
    expect(table).not.toHaveAttribute('inert');
    expect(
      screen.queryByRole('status', { name: 'Loading channels' }),
    ).not.toBeInTheDocument();
    expect(
      await within(table).findByText('Updated channel'),
    ).toBeInTheDocument();
    if (listStatus === 503) {
      expect(mockToast.error).toHaveBeenCalledTimes(1);
      expect(mockToast.error).toHaveBeenCalledWith(
        'Could not refresh channels. Try again.',
      );
    } else {
      expect(mockToast.error).not.toHaveBeenCalled();
    }

    fetchMock.mockImplementation(async (input) => {
      if (input === '/api/channels/registrations')
        return response([registration]);
      if (input === statusPath)
        return response({ registration_id: registration.id, status: 'active' });
      if (input === botsPath) return response({ bots: [bot] });
      throw new Error(`Unexpected test request: ${String(input)}`);
    });
    fireEvent.click(refreshButton);
    expect(refreshButton).toBeDisabled();
    await waitFor(() => expect(refreshButton).toBeEnabled());
    expect(fetchMock).toHaveBeenCalledTimes(6);
    expect(await within(table).findByText('Team channel')).toBeInTheDocument();
  });

  it('loads the connected table, exact Ornn skill link and first-level navigation without enabling unsupported platforms', async () => {
    const pendingStatus = deferred<Response>();
    const pendingNames = deferred<Response>();
    fetchMock.mockImplementation(async (input) =>
      input === botsPath
        ? pendingNames.promise
        : input === statusPath
          ? pendingStatus.promise
          : response([registration]),
    );
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    const table = await screen.findByRole('table', { name: /^Connected/ });
    expect(within(table).getByText('Checking…')).toBeInTheDocument();
    expect(
      within(table).getByRole('columnheader', { name: 'Channel name' }),
    ).toBeInTheDocument();
    expect(
      within(table).getByRole('columnheader', { name: 'Channel' }),
    ).toBeInTheDocument();
    expect(within(table).getByText('Loading name…')).toBeInTheDocument();
    await act(async () =>
      pendingNames.resolve(
        response({
          bots: [
            {
              ...bot,
              id: registration.id,
              label: 'Wrong registration ID match',
            },
            { ...bot, id: 'unrelated-bot', label: 'Unrelated bot' },
            bot,
          ],
        }),
      ),
    );
    expect(await within(table).findByText('Team channel')).toBeInTheDocument();
    expect(
      within(table).getByRole('cell', { name: 'Telegram' }),
    ).toBeInTheDocument();
    expect(
      within(table).queryByText(/Wrong registration|Unrelated bot/),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Open review.skill in Ornn' }),
    ).toHaveAttribute('href', 'https://ornn.chrono-ai.fun/skills/review.skill');
    expect(within(table).getByText('Version 2.4')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Connect Telegram' }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/channels/connect/telegram',
    );
    const availableChannels = screen.getByRole('region', {
      name: 'Available channels',
    });
    expect(
      within(availableChannels)
        .getAllByRole('heading', { level: 3 })
        .map((heading) => heading.textContent),
    ).toEqual(['Telegram', 'WhatsApp']);
    expect(within(availableChannels).getAllByRole('link')).toHaveLength(1);
    expect(within(availableChannels).getAllByText('Soon')).toHaveLength(2);
    expect(screen.getByRole('link', { name: 'Channels' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await act(async () =>
      pendingStatus.resolve(
        response({ registration_id: registration.id, status: 'active' }),
      ),
    );
    expect(await within(table).findByText('Active')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('link', { name: 'Connect Telegram' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/connect/telegram',
    );
    fireEvent.click(within(table).getByRole('link', { name: 'Manage' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/registration%3Aalpha%2Fone',
    );
  });

  it('keeps status failure distinct from an active bot and shows missing skills without a fabricated version', async () => {
    fetchMock.mockImplementation(async (input) =>
      input === botsPath
        ? response({ bots: [bot] })
        : input === statusPath
          ? response({}, 502)
          : response([{ ...registration, default_skill: null }]),
    );
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    const table = await screen.findByRole('table');
    expect(await within(table).findByText('Unknown')).toBeInTheDocument();
    expect(within(table).getByText('Not set')).toBeInTheDocument();
    expect(within(table).queryByText(/Version/)).not.toBeInTheDocument();
    expect(within(table).getByRole('link', { name: 'Manage' })).toBeEnabled();
  });

  it('preserves the connected row when names fail, shows one toast and recovers on manual refresh', async () => {
    let namesAvailable = false;
    fetchMock.mockImplementation(async (input) => {
      if (input === botsPath)
        return namesAvailable ? response({ bots: [bot] }) : response({}, 503);
      if (input === statusPath)
        return response({ registration_id: registration.id, status: 'active' });
      return response([registration]);
    });
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    await screen.findByText('Name unavailable');
    await waitFor(() => expect(mockToast.error).toHaveBeenCalledTimes(1));
    expect(screen.getByText('bot-alpha')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Manage' })).toBeEnabled();
    namesAvailable = true;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh channels' }));
    expect(await screen.findByText('Team channel')).toBeInTheDocument();
    expect(mockToast.error).toHaveBeenCalledTimes(1);
  });

  it('does not use a mismatched platform or a registration ID as a bot label match', async () => {
    fetchMock.mockImplementation(async (input) => {
      if (input === botsPath)
        return response({
          bots: [
            { ...bot, platform: 'lark', label: 'Wrong platform' },
            { ...bot, id: registration.id, label: 'Wrong identity' },
          ],
        });
      if (input === statusPath)
        return response({ registration_id: registration.id, status: 'active' });
      return response([registration]);
    });
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    expect(await screen.findByText('Name unavailable')).toBeInTheDocument();
    expect(
      screen.queryByText(/Wrong platform|Wrong identity/),
    ).not.toBeInTheDocument();
  });

  it('recovers a failed collection query into a genuine empty state', async () => {
    fetchMock
      .mockResolvedValueOnce(response({}, 503))
      .mockResolvedValue(response([]));
    renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Could not load channels',
    );
    expect(
      screen.queryByText('No channels connected yet'),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(
      await screen.findByText('No channels connected yet'),
    ).toBeInTheDocument();
  });

  it('does not flash a previous scope collection when the route scope changes', async () => {
    const next = deferred<Response>();
    const view = renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
    await screen.findByText('bot-alpha');
    fetchMock.mockImplementation(async (input) =>
      input === '/api/channels/registrations'
        ? next.promise
        : response({ registration_id: registration.id, status: 'active' }),
    );
    view.rerender(<div />);
    renderWithQueryClient(
      <ChannelsPage scopeId="scope-beta" />,
      view.queryClient,
    );
    expect(screen.queryByText('bot-alpha')).not.toBeInTheDocument();
    await act(async () => next.resolve(response([])));
    expect(
      await screen.findByText('No channels connected yet'),
    ).toBeInTheDocument();
  });

  it('links exact bot and agent key IDs to NyxID, hides Scope and keeps removal safe', async () => {
    fetchMock.mockImplementation(async (input) =>
      input === statusPath
        ? response({ registration_id: registration.id, status: 'active' })
        : response([
            {
              ...registration,
              nyx_channel_bot_id: 'bot:alpha/one',
              nyx_agent_api_key_id: 'key:alpha/two',
              access_token: 'TEST_ONLY_SECRET',
              webhook_url: 'https://example.invalid/private',
            },
          ]),
    );
    renderWithQueryClient(
      <ChannelDetailsPage
        registrationId={registration.id}
        scopeId="scope-alpha"
      />,
    );
    const keyLink = await screen.findByRole('link', {
      name: 'Open Agent key ID key:alpha/two in NyxID (new tab)',
    });
    const botLink = screen.getByRole('link', {
      name: 'Open Bot ID bot:alpha/one in NyxID (new tab)',
    });
    expect(keyLink).toHaveAttribute(
      'href',
      'https://nyx.chrono-ai.fun/keys/api-key/key%3Aalpha%2Ftwo',
    );
    expect(botLink).toHaveAttribute(
      'href',
      'https://nyx.chrono-ai.fun/channel-bots/bot%3Aalpha%2Fone',
    );
    for (const link of [botLink, keyLink]) {
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    }
    expect(screen.queryByText('Scope')).not.toBeInTheDocument();
    expect(screen.queryByText('scope-alpha')).not.toBeInTheDocument();
    expect(screen.getByText('telegram-provider')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Open review.skill in Ornn' }),
    ).toHaveAttribute('href', 'https://ornn.chrono-ai.fun/skills/review.skill');
    expect(screen.getByText('Version 2.4')).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
    expect(document.body).not.toHaveTextContent('example.invalid');
    expect(
      screen.queryByRole('button', { name: /Repair|Test reply|Replace|Save/ }),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Remove this channel?',
    });
    expect(dialog).toHaveTextContent('bot:alpha/one');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(
      fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE'),
    ).toBe(false);
  });

  it('keeps missing bot and agent key IDs as unlinked placeholders', async () => {
    fetchMock.mockImplementation(async (input) =>
      input === statusPath
        ? response({ registration_id: registration.id, status: 'active' })
        : response([
            {
              ...registration,
              nyx_channel_bot_id: null,
              nyx_agent_api_key_id: null,
              default_skill: null,
            },
          ]),
    );
    renderWithQueryClient(
      <ChannelDetailsPage
        registrationId={registration.id}
        scopeId="scope-alpha"
      />,
    );
    const details = await screen.findByRole('region', {
      name: 'Channel details',
    });
    expect(within(details).getAllByText('—')).toHaveLength(2);
    expect(within(details).getByText('Not set')).toBeInTheDocument();
    expect(within(details).queryByRole('link')).not.toBeInTheDocument();
  });

  it('waits for list readback after removal and Check again never repeats DELETE', async () => {
    jest.useFakeTimers();
    try {
      let deletedFromList = false;
      fetchMock.mockImplementation(async (input, init) => {
        if (input === deletePath && init?.method === 'DELETE')
          return response({ status: 'deleted', warnings: [] });
        if (input === statusPath)
          return response({
            registration_id: registration.id,
            status: 'active',
          });
        return response(deletedFromList ? [] : [registration]);
      });
      renderWithQueryClient(
        <ChannelDetailsPage
          registrationId={registration.id}
          scopeId="scope-alpha"
        />,
      );
      fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
      fireEvent.click(
        within(await screen.findByRole('dialog')).getByRole('button', {
          name: 'Remove',
        }),
      );
      expect(await screen.findByText(/Removal requested/)).toBeInTheDocument();
      expect(mockToast.success).not.toHaveBeenCalled();
      expect(history.replace).not.toHaveBeenCalled();
      expect(screen.getByRole('button', { name: 'Remove' })).toBeDisabled();
      const requestsAfterRemoval = fetchMock.mock.calls.length;
      await act(async () => {
        await jest.advanceTimersByTimeAsync(60_000);
        focusManager.setFocused(false);
        focusManager.setFocused(true);
        onlineManager.setOnline(false);
        onlineManager.setOnline(true);
        await jest.advanceTimersByTimeAsync(1);
      });
      expect(fetchMock).toHaveBeenCalledTimes(requestsAfterRemoval);
      deletedFromList = true;
      fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
      await waitFor(() =>
        expect(history.replace).toHaveBeenCalledWith(
          '/scopes/scope-alpha/workflow-activity-vnext/channels',
        ),
      );
      expect(mockToast.success).toHaveBeenCalledWith('Channel removed.');
      expect(
        fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE'),
      ).toHaveLength(1);
    } finally {
      focusManager.setFocused(undefined);
      onlineManager.setOnline(true);
      jest.useRealTimers();
    }
  });

  it('keeps failed removal retryable and reports cleanup warnings without exposing their content', async () => {
    let removalCount = 0;
    fetchMock.mockImplementation(async (input, init) => {
      if (input === deletePath && init?.method === 'DELETE') {
        removalCount += 1;
        return removalCount === 1
          ? response({}, 502)
          : response({ status: 'deleted', warnings: ['TEST_ONLY_SECRET'] });
      }
      if (input === statusPath)
        return response({ registration_id: registration.id, status: 'active' });
      return response(removalCount > 1 ? [] : [registration]);
    });
    renderWithQueryClient(
      <ChannelDetailsPage
        registrationId={registration.id}
        scopeId="scope-alpha"
      />,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Remove' }),
    );
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Could not remove',
    );
    expect(history.replace).not.toHaveBeenCalled();
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Remove' }),
    );
    await waitFor(() => expect(mockToast.warning).toHaveBeenCalled());
    expect(mockToast.success).not.toHaveBeenCalled();
    expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  });

  it('hides unavailable registrations and never queries their status or exposes removal', async () => {
    fetchMock.mockResolvedValue(response([]));
    renderWithQueryClient(
      <ChannelDetailsPage
        registrationId="registration-inaccessible"
        scopeId="scope-alpha"
      />,
    );
    expect(
      await screen.findByText(
        'This channel is unavailable or you do not have access.',
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Remove' }),
    ).not.toBeInTheDocument();
    expect(
      fetchMock.mock.calls.every(
        ([input]) => input === '/api/channels/registrations',
      ),
    ).toBe(true);
  });
});
