import { App } from 'antd';
import type { ArgsProps, MessageInstance } from 'antd/es/message/interface';
import React from 'react';

type ConsoleToastOptions = Pick<ArgsProps, 'duration' | 'key' | 'onClose'>;
type ConsoleToastIntent = 'error' | 'info' | 'success' | 'warning';

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

function createConsoleToastApi(messageApi: MessageInstance): ConsoleToastApi {
  const show = (
    intent: ConsoleToastIntent,
    content: React.ReactNode,
    options?: ConsoleToastOptions,
  ) => {
    void messageApi[intent]({
      content,
      duration: options?.duration ?? TOAST_DURATION[intent],
      key: options?.key,
      onClose: options?.onClose,
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
  const { message } = App.useApp();
  return React.useMemo(() => createConsoleToastApi(message), [message]);
}
