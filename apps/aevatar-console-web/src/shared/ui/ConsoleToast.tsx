import { App, theme } from 'antd';
import type {
  ArgsProps,
  NotificationInstance,
} from 'antd/es/notification/interface';
import React from 'react';

type ConsoleToastOptions = Pick<ArgsProps, 'duration' | 'key' | 'onClose'>;
type ConsoleToastIntent = 'error' | 'info' | 'success' | 'warning';

type ConsoleToastSurfaceToken = Pick<
  ReturnType<typeof theme.useToken>['token'],
  | 'borderRadiusLG'
  | 'boxShadowSecondary'
  | 'colorBorderSecondary'
  | 'fontSize'
  | 'lineHeight'
  | 'padding'
  | 'paddingSM'
>;

export type ConsoleToastApi = {
  readonly [Intent in ConsoleToastIntent]: (
    content: React.ReactNode,
    options?: ConsoleToastOptions,
  ) => void;
};

const TOAST_DURATION: Readonly<Record<ConsoleToastIntent, number>> = {
  error: 5,
  info: 3,
  success: 3,
  warning: 5,
};

function createConsoleToastApi(
  notificationApi: NotificationInstance,
  token: ConsoleToastSurfaceToken,
): ConsoleToastApi {
  const show = (
    intent: ConsoleToastIntent,
    content: React.ReactNode,
    options?: ConsoleToastOptions,
  ) => {
    notificationApi[intent]({
      className: 'aevatar-console-toast',
      closable: true,
      duration: options?.duration ?? TOAST_DURATION[intent],
      key: options?.key,
      onClose: options?.onClose,
      pauseOnHover: true,
      placement: 'topRight',
      role: intent === 'error' || intent === 'warning' ? 'alert' : 'status',
      styles: {
        root: {
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadiusLG,
          boxShadow: token.boxShadowSecondary,
          maxWidth: 'calc(100vw - 32px)',
          padding: `${token.paddingSM}px ${token.padding}px`,
          width: 360,
        },
        title: {
          fontSize: token.fontSize,
          lineHeight: token.lineHeight,
          marginBottom: 0,
        },
      },
      title: content,
    });
  };

  return {
    error: (content, options) => show('error', content, options),
    info: (content, options) => show('info', content, options),
    success: (content, options) => show('success', content, options),
    warning: (content, options) => show('warning', content, options),
  };
}

export const ConsoleToastProvider: React.FC<{
  readonly children: React.ReactNode;
}> = ({ children }) => (
  <App component="div" style={{ display: 'contents' }}>
    {children}
  </App>
);

export function useConsoleToast(): ConsoleToastApi {
  const { notification } = App.useApp();
  const { token } = theme.useToken();
  return React.useMemo(
    () => createConsoleToastApi(notification, token),
    [notification, token],
  );
}
