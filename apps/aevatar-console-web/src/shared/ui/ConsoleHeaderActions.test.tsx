import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { getLocale, setLocale } from '@umijs/max';
import React from 'react';
import {
  clearStoredAuthSession,
  persistAuthSession,
} from '@/shared/auth/session';
import { loadConsoleLocale } from '@/shared/i18n/loadConsoleLocale';
import { ConsoleToastProvider } from './ConsoleToast';
import {
  ConsoleAuthActions,
  ConsoleHeaderActions,
} from './ConsoleHeaderActions';

const mockedHistoryPush = jest.fn();
const mockedLoad = jest.mocked(loadConsoleLocale);

// Umi emits non-configurable export getters; mock the module boundary instead
// of trying to replace its named export with spyOn.
jest.mock('@/shared/i18n/loadConsoleLocale', () => ({
  loadConsoleLocale: jest.fn(),
}));

jest.mock('@/shared/navigation/history', () => ({
  history: {
    push: (...args: unknown[]) => mockedHistoryPush(...args),
  },
}));

describe('ConsoleHeaderActions', () => {
  beforeEach(() => {
    clearStoredAuthSession();
    mockedHistoryPush.mockReset();
    mockedLoad.mockReset().mockImplementation(
      jest.requireActual<typeof import('@/shared/i18n/loadConsoleLocale')>(
        '@/shared/i18n/loadConsoleLocale',
      ).loadConsoleLocale,
    );
    setLocale('en-US', false);
    window.history.replaceState(
      {},
      '',
      '/runtime/mission-wall?focusRunId=run-1',
    );
  });

  afterEach(() => {
    clearStoredAuthSession();
  });

  it('preserves the current language while downloading or on failure, then allows retry', async () => {
    let fail!: (error: Error) => void;
    let finish!: () => void;
    const download = new Promise<void>((_resolve, reject) => {
      fail = reject;
    });
    const retry = new Promise<void>((resolve) => {
      finish = resolve;
    });
    mockedLoad
      .mockReturnValueOnce(download)
      .mockReturnValueOnce(retry);
    render(
      React.createElement(
        ConsoleToastProvider,
        null,
        React.createElement(ConsoleHeaderActions),
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Switch language' }));
    const chineseOption = screen.getByText('中文');
    fireEvent.click(chineseOption);
    fireEvent.click(chineseOption);
    expect(getLocale()).toBe('en-US');
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(mockedLoad).toHaveBeenCalledTimes(1);

    await act(async () => fail(new Error('Offline')));
    expect(getLocale()).toBe('en-US');
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Could not load this language. Please try again.',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Switch language' }));
    fireEvent.click(screen.getByText('中文'));
    expect(getLocale()).toBe('en-US');
    await act(async () => finish());
    expect(getLocale()).toBe('zh-CN');
    expect(mockedLoad).toHaveBeenCalledTimes(2);
  });

  it('renders a login entry when there is no restorable auth session', () => {
    render(React.createElement(ConsoleHeaderActions));

    expect(
      screen.getByRole('button', { name: 'Switch language' }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(mockedHistoryPush).toHaveBeenCalledWith(
      '/login?redirect=%2Fruntime%2Fmission-wall%3FfocusRunId%3Drun-1',
    );
  });

  it('keeps the language switch and authenticated user menu together', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        email: 'abigail@example.com',
        name: 'Abigail Deng',
        picture: 'https://example.com/avatar.png',
        sub: 'user-abigail',
      },
    });

    render(React.createElement(ConsoleHeaderActions));

    fireEvent.click(screen.getByRole('button', { name: 'Switch language' }));
    fireEvent.click(screen.getByText('中文'));

    await waitFor(() => expect(getLocale()).toBe('zh-CN'));
    expect(screen.getByText('Abigail Deng')).toBeInTheDocument();
  });

  it('renders a supplied authoritative principal instead of the stored session identity', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        name: 'Stale Browser Name',
        sub: 'stored-user',
      },
    });

    render(
      React.createElement(ConsoleAuthActions, {
        principal: {
          authenticated: true,
          displayName: 'Authoritative Account Name',
          picture: null,
        },
      }),
    );

    expect(screen.getByText('Authoritative Account Name')).toBeInTheDocument();
    expect(screen.queryByText('Stale Browser Name')).not.toBeInTheDocument();
  });

  it('does not fall back to a stored identity when the authoritative principal is unavailable', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        name: 'Stale Browser Name',
        sub: 'stored-user',
      },
    });

    render(React.createElement(ConsoleAuthActions, { principal: null }));

    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Stale Browser Name')).not.toBeInTheDocument();
  });

  it('clears a stale stored session before authoritative sign-in recovery', () => {
    persistAuthSession({
      tokens: {
        accessToken: 'stale-token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        name: 'Stale Browser Name',
        sub: 'stored-user',
      },
    });

    render(React.createElement(ConsoleAuthActions, { principal: null }));

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(
      window.localStorage.getItem('aevatar-console:nyxid:session'),
    ).toBeNull();
    expect(mockedHistoryPush).toHaveBeenCalledWith(
      '/login?redirect=%2Fruntime%2Fmission-wall%3FfocusRunId%3Drun-1',
    );
  });

  it('applies an optional dropdown root class to action menus', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        email: 'abigail@example.com',
        name: 'Abigail Deng',
        picture: 'https://example.com/avatar.png',
        sub: 'user-abigail',
      },
    });

    render(
      React.createElement(ConsoleHeaderActions, {
        dropdownRootClassName: 'mission-wall-header-menu',
      }),
    );

    expect(document.querySelector('.console-header-actions')).toHaveAttribute(
      'data-dropdown-root-class-name',
      'mission-wall-header-menu',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Switch language' }));

    expect(await screen.findByText('中文')).toBeInTheDocument();
    expect(
      document.querySelector('.mission-wall-header-menu'),
    ).toBeInTheDocument();
  });
});
