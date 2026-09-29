import { App, theme } from 'antd';
import type {
  ArgsProps,
  NotificationInstance,
} from 'antd/es/notification/interface';
import React from 'react';
import { AEVATAR_GLOBAL_UI_SPEC } from './aevatarWorkbench';

type ConsoleToastOptions = Pick<ArgsProps, 'duration' | 'key' | 'onClose'>;
type ConsoleToastIntent = 'error' | 'info' | 'success' | 'warning';

type ConsoleToastSurfaceToken = Pick<
  ReturnType<typeof theme.useToken>['token'],
  | 'borderRadiusLG'
  | 'colorBorderSecondary'
  | 'fontSize'
  | 'lineHeight'
  | 'padding'
  | 'paddingSM'
  | 'paddingXL'
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

const CONSOLE_TOAST_TOP = AEVATAR_GLOBAL_UI_SPEC.tokens.headerHeight + 12;

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
          maxWidth: 'min(360px, calc(100vw - 32px))',
          padding: `${token.paddingSM}px ${token.padding}px`,
          width: 'max-content',
        },
        title: {
          fontSize: token.fontSize,
          lineHeight: token.lineHeight,
          marginBottom: 0,
          paddingInlineEnd: token.paddingXL,
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
  <App
    component="div"
    notification={{ placement: 'topRight', top: CONSOLE_TOAST_TOP }}
    style={{ display: 'contents' }}
  >
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
