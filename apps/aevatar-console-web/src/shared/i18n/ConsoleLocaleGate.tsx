import { getLocale } from '@umijs/max';
import { Alert, Button } from 'antd';
import React from 'react';
import enUS from '@/locales/en-US';
import zhCN from '@/locales/zh-CN';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import type { ConsoleCatalogLocale } from './createLocaleCatalogLoader';
import { loadConsoleLocale } from './loadConsoleLocale';

/** Mount Umi's providers and pages only after the chosen catalog is registered. */
export function ConsoleLocaleGate({
  children,
}: {
  readonly children: React.ReactNode;
}) {
  // Match Umi's configured default when storage contains an unsupported locale.
  const [locale] = React.useState<ConsoleCatalogLocale>(() =>
    getLocale() === 'zh-CN' ? 'zh-CN' : 'en-US',
  );
  const [state, setState] = React.useState<'loading' | 'ready' | 'failed'>(
    'loading',
  );
  const [attempt, setAttempt] = React.useState(0);
  const messages = locale === 'en-US' ? enUS : zhCN;

  React.useEffect(() => {
    let active = true;
    loadConsoleLocale(locale).then(
      () => {
        if (active) setState('ready');
      },
      () => {
        if (active) setState('failed');
      },
    );
    return () => {
      active = false;
    };
  }, [attempt, locale]);

  if (state === 'ready') return <>{children}</>;
  if (state === 'loading') {
    return (
      <AevatarPageLoading
        ariaLabel={messages['common.locale.loading']}
        fullscreen
      />
    );
  }
  return (
    <Alert
      action={
        <Button
          onClick={() => {
            setState('loading');
            setAttempt((value) => value + 1);
          }}
        >
          {messages['common.locale.retry']}
        </Button>
      }
      role="alert"
      showIcon
      title={messages['common.locale.loadFailed']}
      type="error"
    />
  );
}
