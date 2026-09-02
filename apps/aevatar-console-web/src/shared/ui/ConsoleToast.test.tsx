import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
} from '@testing-library/react';
import { App } from 'antd';
import type { NotificationInstance } from 'antd/es/notification/interface';
import React from 'react';
import { ConsoleToastProvider, useConsoleToast } from './ConsoleToast';

function createNotificationStub(): jest.Mocked<NotificationInstance> {
  return {
    destroy: jest.fn(),
    error: jest.fn(),
    info: jest.fn(),
    open: jest.fn(),
    success: jest.fn(),
    warning: jest.fn(),
  };
}

const PublishToastTrigger: React.FC = () => {
  const toast = useConsoleToast();
  return (
    <button
      onClick={() =>
        toast.success('Workflow published', {
          duration: false,
          key: 'workflow-published',
        })
      }
      type="button"
    >
      Show publish status
    </button>
  );
};

describe('ConsoleToast', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('renders a compact status below the console header', async () => {
    render(
      <ConsoleToastProvider>
        <PublishToastTrigger />
      </ConsoleToastProvider>,
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Show publish status' }),
    );

    const status = await screen.findByRole('status');
    const notice = status.closest('.ant-notification-notice');
    const holder = notice?.closest('.ant-notification-topRight');

    if (!(notice instanceof HTMLElement)) {
      throw new Error('Expected the notification notice to be an HTML element');
    }
    expect(notice).toHaveStyle({
      maxWidth: 'min(360px, calc(100vw - 32px))',
      width: 'max-content',
    });
    expect(notice.style.boxShadow).toBe('');
    expect(holder).toHaveStyle({ top: '68px' });
    expect(screen.getByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('opens a compact dismissible top-right error notification', () => {
    const notification = createNotificationStub();
    const onClose = jest.fn();
    const content = <span>Request failed</span>;
    jest.spyOn(App, 'useApp').mockReturnValue({
      notification,
    } as unknown as ReturnType<typeof App.useApp>);

    const { result } = renderHook(() => useConsoleToast());
    act(() => {
      result.current.error(content, {
        duration: 8,
        key: 'request-failure',
        onClose,
      });
    });

    expect(notification.error).toHaveBeenCalledTimes(1);
    const config = notification.error.mock.calls[0][0];
    expect(config).toMatchObject({
      className: 'aevatar-console-toast',
      closable: true,
      duration: 8,
      key: 'request-failure',
      onClose,
      pauseOnHover: true,
      placement: 'topRight',
      role: 'alert',
    });
    expect(config.title).toBe(content);
    expect(config.styles).toMatchObject({
      root: {
        maxWidth: 'min(360px, calc(100vw - 32px))',
        width: 'max-content',
      },
      title: {
        marginBottom: 0,
      },
    });
    if (!config.styles || typeof config.styles === 'function') {
      throw new Error(
        'Expected notification styles to be a semantic style map',
      );
    }
    expect(config.styles.root).not.toHaveProperty('boxShadow');
  });

  it('uses status semantics and the short default for success', () => {
    const notification = createNotificationStub();
    jest.spyOn(App, 'useApp').mockReturnValue({
      notification,
    } as unknown as ReturnType<typeof App.useApp>);

    const { result } = renderHook(() => useConsoleToast());
    act(() => {
      result.current.success('Saved');
    });

    expect(notification.success).toHaveBeenCalledWith(
      expect.objectContaining({ duration: 3, role: 'status' }),
    );
  });
});
