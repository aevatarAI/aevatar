import { act, renderHook } from '@testing-library/react';
import { App } from 'antd';
import type { NotificationInstance } from 'antd/es/notification/interface';
import React from 'react';
import { useConsoleToast } from './ConsoleToast';

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

describe('ConsoleToast', () => {
  afterEach(() => {
    jest.restoreAllMocks();
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
        maxWidth: 'calc(100vw - 32px)',
        width: 360,
      },
      title: {
        marginBottom: 0,
      },
    });
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
