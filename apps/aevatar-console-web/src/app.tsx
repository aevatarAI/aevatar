import { ProConfigProvider } from '@ant-design/pro-components';
import { QueryClientProvider } from '@tanstack/react-query';
import { getLocale, useIntl } from '@umijs/max';
import { ConfigProvider } from 'antd';
import React from 'react';
import {
  normalizeConsoleLocale,
  resolveAntdLocale,
  resolveProIntl,
} from '@/shared/i18n/localeProvider';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { aevatarThemeConfig } from '@/shared/ui/aevatarWorkbench';
import { ConsoleToastProvider } from '@/shared/ui/ConsoleToast';
import defaultSettings from '../config/defaultSettings';
import { errorConfig } from './requestErrorConfig';
import {
  ensureActiveAuthSession,
  hasRestorableAuthSession,
} from './shared/auth/client';
import { getNyxIDRuntimeConfig } from './shared/auth/config';
import { ProtectedRouteRedirectGate } from './shared/auth/ProtectedRouteRedirectGate';
import {
  buildAuthInitialState,
  loadStoredAuthSession,
  sanitizeReturnTo,
} from './shared/auth/session';
import { history } from './shared/navigation/history';
import { queryClient } from './shared/query/queryClient';

const PUBLIC_ROUTES = new Set([
  '/login',
  '/auth/callback',
  ...(process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK === '1'
    ? ['/workflow-canvas-benchmark']
    : []),
]);

export async function getInitialState(): Promise<{
  settings: typeof defaultSettings;
  auth: ReturnType<typeof buildAuthInitialState>;
}> {
  return {
    settings: defaultSettings,
    auth: buildAuthInitialState(getNyxIDRuntimeConfig()),
  };
}

type RuntimeInitialState = Awaited<ReturnType<typeof getInitialState>>;

const AuthSessionBootstrap: React.FC<{
  pathname: string;
  children: React.ReactNode;
}> = ({ pathname, children }) => {
  const [ready, setReady] = React.useState(() =>
    Boolean(loadStoredAuthSession()),
  );
  React.useEffect(() => {
    let cancelled = false;
    if (loadStoredAuthSession()) {
      setReady(true);
      return undefined;
    }
    setReady(false);
    void ensureActiveAuthSession().then((session) => {
      if (cancelled) return;
      if (!session) {
        const returnTo =
          pathname === '/'
            ? CONSOLE_HOME_ROUTE
            : `${pathname}${window.location.search}${window.location.hash}`;
        const params = new URLSearchParams({
          redirect: sanitizeReturnTo(returnTo),
        });
        history.replace(`/login?${params.toString()}`);
        return;
      }
      setReady(true);
    });
    return () => {
      cancelled = true;
    };
  }, [pathname]);
  return ready ? children : <AevatarPageLoading fullscreen />;
};

const ConsoleRuntimeProviders: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const intl = useIntl();
  const locale = normalizeConsoleLocale(intl.locale || getLocale());
  return (
    <ConfigProvider
      button={{ autoInsertSpace: false }}
      locale={resolveAntdLocale(locale)}
      theme={aevatarThemeConfig}
    >
      <ProConfigProvider intl={resolveProIntl(locale)}>
        <ConsoleToastProvider>
          <QueryClientProvider client={queryClient}>
            <React.Fragment key={locale}>{children}</React.Fragment>
          </QueryClientProvider>
        </ConsoleToastProvider>
      </ProConfigProvider>
    </ConfigProvider>
  );
};

export const layout = ({
  initialState,
}: {
  initialState?: RuntimeInitialState;
}): Record<string, unknown> => ({
  ...initialState?.settings,
  title: '',
  headerRender: false,
  menuRender: () => false,
  actionsRender: () => [],
  contentStyle: {
    background: '#ffffff',
    display: 'block',
    height: 'auto',
    inset: 0,
    minHeight: 0,
    overflow: 'hidden',
    padding: 0,
    position: 'fixed',
    width: '100%',
  },
  onPageChange: () => {
    if (window.location.pathname === '/') history.replace(CONSOLE_HOME_ROUTE);
  },
  childrenRender: (children: React.ReactNode) => {
    if (!initialState) return <AevatarPageLoading fullscreen />;
    const pathname = window.location.pathname;
    const isPublicRoute = PUBLIC_ROUTES.has(pathname);
    const liveSession = loadStoredAuthSession();
    const content =
      !isPublicRoute && !liveSession ? (
        hasRestorableAuthSession() ? (
          <AuthSessionBootstrap pathname={pathname}>
            {children}
          </AuthSessionBootstrap>
        ) : (
          <ProtectedRouteRedirectGate pathname={pathname} />
        )
      ) : (
        children
      );
    return <ConsoleRuntimeProviders>{content}</ConsoleRuntimeProviders>;
  },
});

export const request: Record<string, unknown> = { ...errorConfig };
