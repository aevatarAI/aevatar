import { ProConfigProvider } from '@ant-design/pro-components';
import { QueryClientProvider } from '@tanstack/react-query';
import { getLocale, useIntl } from '@umijs/max';
import { Badge, ConfigProvider } from 'antd';
import React from 'react';
import BrandLogo from '@/components/BrandLogo';
import type { LiveOpsAttentionSnapshot } from '@/layouts/liveOpsAttention';
import MainLayout from '@/layouts/MainLayout';
import {
  initializeConsoleAnalytics,
  syncConsoleAnalyticsUser,
} from '@/shared/analytics/posthog';
import { ConsoleLocaleGate } from '@/shared/i18n/ConsoleLocaleGate';
import {
  normalizeConsoleLocale,
  resolveAntdLocale,
  resolveProIntl,
} from '@/shared/i18n/localeProvider';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { aevatarThemeConfig } from '@/shared/ui/aevatarWorkbench';
import { ConsoleHeaderActions } from '@/shared/ui/ConsoleHeaderActions';
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
  PUBLIC_ROUTES,
  requiresGlobalAuthGate,
} from './shared/auth/routeAccess';
import {
  buildAuthInitialState,
  loadRestorableAuthSession,
  loadStoredAuthSession,
  sanitizeReturnTo,
} from './shared/auth/session';
import { history } from './shared/navigation/history';
import {
  getNavigationGroupOrder,
  type NavigationGroup,
} from './shared/navigation/navigationGroups';
import {
  groupNavigationMenuItems,
  type NavigationMenuItem,
} from './shared/navigation/navigationMenuGrouping';
import { getNavigationSelectedKeys } from './shared/navigation/navigationMenuSelection';
import { queryClient } from './shared/query/queryClient';

const DEFAULT_PROTECTED_ROUTE = CONSOLE_HOME_ROUTE;

export function rootContainer(container: React.ReactNode) {
  return <ConsoleLocaleGate>{container}</ConsoleLocaleGate>;
}
const FULLSCREEN_DISPLAY_ROUTES = new Set(['/runtime/mission-wall']);
const WORKFLOW_ACTIVITY_VNEXT_ROUTE =
  /^\/scopes\/[^/]+\/(?:workflows|activity|channels|settings)(?:\/|$)/;
const STUDIO_HOST_ROUTES = new Set([
  '/studio',
  '/scopes/:scopeId/teams/:teamId/members/new/workflow',
  '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
]);

function isFullscreenDisplayRoute(pathname: string): boolean {
  return (
    FULLSCREEN_DISPLAY_ROUTES.has(pathname) ||
    isWorkflowActivityVNextRoute(pathname)
  );
}

function isWorkflowActivityVNextRoute(pathname: string): boolean {
  return (
    pathname === CONSOLE_HOME_ROUTE ||
    WORKFLOW_ACTIVITY_VNEXT_ROUTE.test(pathname)
  );
}

function isStudioHostRoute(pathname: string): boolean {
  if (STUDIO_HOST_ROUTES.has(pathname)) {
    return true;
  }

  return /^\/scopes\/[^/]+\/teams\/[^/]+\/members\/(?:new|[^/]+)\/workflow$/.test(
    pathname,
  );
}

function shouldDefaultCollapseLayout(
  pathname: string,
  search: string,
): boolean {
  if (!isStudioHostRoute(pathname)) {
    return false;
  }

  return new URLSearchParams(search).get('intent') === 'create-member';
}

function shouldCollapseLayout(pathname: string, search: string): boolean {
  return shouldDefaultCollapseLayout(pathname, search);
}

function buildLoginRoute(returnTo: string): string {
  const params = new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  });
  return `/login?${params.toString()}`;
}

function getCurrentReturnTo(pathname: string): string {
  return pathname === '/'
    ? DEFAULT_PROTECTED_ROUTE
    : `${pathname}${window.location.search}${window.location.hash}`;
}

/**
 * @see https://umijs.org/docs/api/runtime-config#getinitialstate
 * */
export async function getInitialState(): Promise<{
  settings: typeof defaultSettings;
  auth: ReturnType<typeof buildAuthInitialState>;
}> {
  initializeConsoleAnalytics();
  syncConsoleAnalyticsUser(loadRestorableAuthSession()?.user.sub);
  const authConfig = getNyxIDRuntimeConfig();

  return {
    settings: defaultSettings,
    auth: buildAuthInitialState(authConfig),
  };
}

type RuntimeInitialState = Awaited<ReturnType<typeof getInitialState>>;
type LayoutRuntimeProps = {
  initialState?: RuntimeInitialState;
};

type AuthSessionBootstrapProps = {
  pathname: string;
  children: React.ReactNode;
};

type ConsoleRuntimeProvidersProps = {
  children: React.ReactNode;
  isFullscreenDisplayRoute: boolean;
  isPublicRoute: boolean;
  isStudioRoute: boolean;
  pathname: string;
  search: string;
};

const LIVE_OPS_ATTENTION_BADGE_KEY = 'live.attention';
const LIVE_OPS_ATTENTION_REFRESH_MS = 30_000;
const NAVIGATION_GROUP_ORDER: readonly NavigationGroup[] =
  getNavigationGroupOrder();
const NAVIGATION_MENU_MESSAGE_IDS: Readonly<Record<string, string>> = {
  '/chat': 'nav.items.chat',
  '/scopes': 'nav.items.myTeams',
  '/runtime/runs': 'nav.items.eventStream',
  '/services': 'nav.items.services',
  '/governance': 'nav.items.governance',
  '/deployments': 'nav.items.deployments',
  '/runtime/explorer': 'nav.items.topology',
  '/settings': 'nav.items.settings',
};
const LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT: LiveOpsAttentionSnapshot = {
  hasPendingAttention: false,
  pendingCount: 0,
};
const liveOpsAttentionListeners = new Set<() => void>();
let liveOpsAttentionSnapshot = LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT;

const navigationGroupLabelStyle: React.CSSProperties = {
  color: '#667085',
  display: 'inline-flex',
  fontSize: 14,
  fontWeight: 700,
  lineHeight: '22px',
};

const LocalizedNavigationText: React.FC<{
  defaultLabel?: React.ReactNode;
  messageId: string;
}> = ({ defaultLabel, messageId }) => {
  const intl = useIntl();
  const defaultMessage =
    typeof defaultLabel === 'string' ? defaultLabel : undefined;

  return (
    <>
      {intl.formatMessage({
        defaultMessage,
        id: messageId,
      })}
    </>
  );
};

const NavigationGroupLabel: React.FC<{
  group: NavigationGroup;
}> = ({ group }) => (
  <span style={navigationGroupLabelStyle}>
    <LocalizedNavigationText
      defaultLabel={group.label}
      messageId={group.labelMessageId}
    />
  </span>
);

function subscribeLiveOpsAttention(listener: () => void): () => void {
  liveOpsAttentionListeners.add(listener);
  return () => {
    liveOpsAttentionListeners.delete(listener);
  };
}

function getLiveOpsAttentionSnapshot(): LiveOpsAttentionSnapshot {
  return liveOpsAttentionSnapshot;
}

function setLiveOpsAttentionSnapshot(next: LiveOpsAttentionSnapshot): void {
  if (
    liveOpsAttentionSnapshot.pendingCount === next.pendingCount &&
    liveOpsAttentionSnapshot.hasPendingAttention === next.hasPendingAttention
  ) {
    return;
  }

  liveOpsAttentionSnapshot = next;
  liveOpsAttentionListeners.forEach((listener) => {
    listener();
  });
}

const NavigationMenuLabel: React.FC<{
  badgeKey?: string;
  label: React.ReactNode;
  showLiveOpsDot?: boolean;
}> = React.memo(({ badgeKey, label, showLiveOpsDot = false }) => {
  const snapshot = React.useSyncExternalStore(
    subscribeLiveOpsAttention,
    getLiveOpsAttentionSnapshot,
    getLiveOpsAttentionSnapshot,
  );
  const showCountBadge =
    badgeKey === LIVE_OPS_ATTENTION_BADGE_KEY && snapshot.pendingCount > 0;

  return (
    <span
      style={{
        alignItems: 'center',
        display: 'inline-flex',
        gap: 8,
        justifyContent: 'space-between',
        minWidth: 0,
        width: '100%',
      }}
    >
      <span
        style={{
          alignItems: 'center',
          display: 'inline-flex',
          gap: 8,
          minWidth: 0,
        }}
      >
        <span
          style={{
            minWidth: 0,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {label}
        </span>
        {showLiveOpsDot && snapshot.hasPendingAttention ? (
          <span
            aria-hidden="true"
            style={{
              background: '#ef4444',
              borderRadius: 999,
              display: 'inline-block',
              flex: '0 0 auto',
              height: 8,
              width: 8,
            }}
          />
        ) : null}
      </span>
      {showCountBadge ? (
        <Badge
          count={snapshot.pendingCount}
          overflowCount={9}
          size="small"
          style={{
            backgroundColor: '#ef4444',
            boxShadow: 'none',
          }}
        />
      ) : null}
    </span>
  );
});

NavigationMenuLabel.displayName = 'NavigationMenuLabel';

const LiveOpsGroupIcon: React.FC<{
  icon: React.ReactNode;
}> = React.memo(({ icon }) => {
  const snapshot = React.useSyncExternalStore(
    subscribeLiveOpsAttention,
    getLiveOpsAttentionSnapshot,
    getLiveOpsAttentionSnapshot,
  );

  if (!snapshot.hasPendingAttention || !React.isValidElement(icon)) {
    return <>{icon}</>;
  }

  return (
    <Badge color="#ef4444" dot offset={[-2, 2]}>
      {icon}
    </Badge>
  );
});

LiveOpsGroupIcon.displayName = 'LiveOpsGroupIcon';

function decorateNavigationMenuItems(
  items: NavigationMenuItem[],
  groupItems = true,
): NavigationMenuItem[] {
  const sourceItems = groupItems
    ? groupNavigationMenuItems(items, NAVIGATION_GROUP_ORDER, (group) =>
        React.createElement(NavigationGroupLabel, { group }),
      )
    : items;

  return sourceItems.map((item) => {
    const path = typeof item.path === 'string' ? item.path : undefined;
    const badgeKey =
      typeof item.menuBadgeKey === 'string' ? item.menuBadgeKey : undefined;
    const groupKey =
      typeof item.menuGroupKey === 'string' ? item.menuGroupKey : undefined;
    const nameMessageId =
      path && typeof item.name === 'string'
        ? NAVIGATION_MENU_MESSAGE_IDS[path]
        : undefined;
    const children = Array.isArray(item.children)
      ? decorateNavigationMenuItems(item.children, false)
      : undefined;
    const isLiveOpsGroup =
      groupKey === 'live' && Array.isArray(children) && children.length > 0;
    const hasRenderableIcon = React.isValidElement(item.icon);
    const localizedName = nameMessageId
      ? React.createElement(LocalizedNavigationText, {
          defaultLabel: item.name,
          messageId: nameMessageId,
        })
      : item.name;
    const name =
      badgeKey || isLiveOpsGroup
        ? React.createElement(NavigationMenuLabel, {
            badgeKey,
            label: localizedName,
            showLiveOpsDot: isLiveOpsGroup && !hasRenderableIcon,
          })
        : localizedName;
    const icon =
      isLiveOpsGroup && hasRenderableIcon
        ? React.createElement(LiveOpsGroupIcon, {
            icon: item.icon,
          })
        : item.icon;

    return {
      ...item,
      children,
      icon,
      name,
    };
  });
}

const LiveOpsAttentionBridge: React.FC<{
  enabled: boolean;
  pathname: string;
  search: string;
}> = ({ enabled, pathname, search }) => {
  React.useEffect(() => {
    if (!enabled) {
      setLiveOpsAttentionSnapshot(LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT);
      return undefined;
    }

    let cancelled = false;
    let refreshing = false;

    const refresh = async () => {
      if (refreshing || cancelled) {
        return;
      }

      refreshing = true;
      try {
        // Chunk failures retry on the next refresh without blocking the shell.
        const attention = await import('@/layouts/liveOpsAttention').catch(
          () => null,
        );
        if (cancelled || !attention) {
          return;
        }
        const snapshot = await attention.loadLiveOpsAttentionSnapshot(
          pathname,
          search,
        );
        if (!cancelled) {
          setLiveOpsAttentionSnapshot(snapshot);
        }
      } finally {
        refreshing = false;
      }
    };

    const refreshWhenVisible = () => {
      if (document.visibilityState === 'visible') {
        void refresh();
      }
    };

    const refreshOnFocus = () => {
      void refresh();
    };

    void refresh();
    const intervalId = window.setInterval(() => {
      void refresh();
    }, LIVE_OPS_ATTENTION_REFRESH_MS);
    document.addEventListener('visibilitychange', refreshWhenVisible);
    window.addEventListener('focus', refreshOnFocus);
    window.addEventListener('storage', refreshOnFocus);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
      document.removeEventListener('visibilitychange', refreshWhenVisible);
      window.removeEventListener('focus', refreshOnFocus);
      window.removeEventListener('storage', refreshOnFocus);
    };
  }, [enabled, pathname, search]);

  return null;
};

const AuthSessionBootstrap: React.FC<AuthSessionBootstrapProps> = ({
  pathname,
  children,
}) => {
  const [ready, setReady] = React.useState(() =>
    Boolean(loadStoredAuthSession()),
  );

  React.useEffect(() => {
    let cancelled = false;

    const storedSession = loadStoredAuthSession();
    if (storedSession) {
      syncConsoleAnalyticsUser(storedSession.user.sub);
      setReady(true);
      return undefined;
    }

    setReady(false);
    void ensureActiveAuthSession().then((session) => {
      if (cancelled) {
        return;
      }

      if (!session) {
        syncConsoleAnalyticsUser();
        history.replace(buildLoginRoute(getCurrentReturnTo(pathname)));
        return;
      }

      syncConsoleAnalyticsUser(session.user.sub);
      setReady(true);
    });

    return () => {
      cancelled = true;
    };
  }, [pathname]);

  if (!ready) {
    return <AevatarPageLoading fullscreen />;
  }

  return <>{children}</>;
};

const ConsoleRuntimeProviders: React.FC<ConsoleRuntimeProvidersProps> = ({
  children,
  isFullscreenDisplayRoute,
  isPublicRoute,
  isStudioRoute,
  pathname,
  search,
}) => {
  const intl = useIntl();
  const currentLocale = normalizeConsoleLocale(intl.locale || getLocale());
  const localizedContent =
    isPublicRoute || isFullscreenDisplayRoute ? (
      children
    ) : (
      <MainLayout>{children}</MainLayout>
    );

  return (
    <ConfigProvider
      button={{ autoInsertSpace: false }}
      locale={resolveAntdLocale(currentLocale)}
      theme={aevatarThemeConfig}
    >
      <ProConfigProvider intl={resolveProIntl(currentLocale)}>
        <ConsoleToastProvider>
          <QueryClientProvider client={queryClient}>
            <LiveOpsAttentionBridge
              enabled={
                !isPublicRoute &&
                !isStudioRoute &&
                !isWorkflowActivityVNextRoute(pathname)
              }
              pathname={pathname}
              search={search}
            />
            <React.Fragment key={currentLocale}>
              {localizedContent}
            </React.Fragment>
          </QueryClientProvider>
        </ConsoleToastProvider>
      </ProConfigProvider>
    </ConfigProvider>
  );
};

// ProLayout runtime API: https://procomponents.ant.design/components/layout
export const layout = ({
  initialState,
}: LayoutRuntimeProps): Record<string, unknown> => {
  const pathname = window.location.pathname;
  const search = window.location.search;
  const collapseForRoute = shouldCollapseLayout(pathname, search);
  const fullscreenDisplayRoute = isFullscreenDisplayRoute(pathname);
  const workflowActivityVNextRoute = isWorkflowActivityVNextRoute(pathname);

  return {
    onPageChange: () => {
      syncConsoleAnalyticsUser(loadRestorableAuthSession()?.user.sub);
      const pathname = window.location.pathname;
      if (PUBLIC_ROUTES.has(pathname)) {
        return;
      }

      if (isStudioHostRoute(pathname)) {
        return;
      }

      if (pathname === '/') {
        history.replace(DEFAULT_PROTECTED_ROUTE);
      }
    },
    postMenuData: (menuData: NavigationMenuItem[]) =>
      decorateNavigationMenuItems(menuData),
    menuRender: (_: unknown, defaultDom: React.ReactNode) => {
      if (isFullscreenDisplayRoute(window.location.pathname)) {
        return false;
      }

      if (!React.isValidElement(defaultDom)) {
        return defaultDom;
      }

      return React.cloneElement(
        defaultDom as React.ReactElement<{ selectedKeys?: string[] }>,
        {
          selectedKeys: getNavigationSelectedKeys(window.location.pathname),
        },
      );
    },
    actionsRender: () => {
      if (isFullscreenDisplayRoute(window.location.pathname)) {
        return [];
      }

      return [<ConsoleHeaderActions key="header-actions" />];
    },
    childrenRender: (children: React.ReactNode) =>
      initialState ? (
        (() => {
          const pathname = window.location.pathname;
          const search = window.location.search;
          const isPublicRoute = PUBLIC_ROUTES.has(pathname);
          const isStudioRoute = isStudioHostRoute(pathname);
          const isDisplayRoute = isFullscreenDisplayRoute(pathname);
          const requiresGlobalAuth = requiresGlobalAuthGate(pathname);
          const liveSession = loadStoredAuthSession();
          const needsProtectedRouteRedirect =
            requiresGlobalAuth && !liveSession && !hasRestorableAuthSession();

          const content = needsProtectedRouteRedirect ? (
            <ProtectedRouteRedirectGate pathname={pathname} />
          ) : requiresGlobalAuth && !liveSession ? (
            <AuthSessionBootstrap pathname={pathname}>
              {children}
            </AuthSessionBootstrap>
          ) : (
            children
          );
          return (
            <ConsoleRuntimeProviders
              isFullscreenDisplayRoute={isDisplayRoute}
              isPublicRoute={isPublicRoute}
              isStudioRoute={isStudioRoute}
              pathname={pathname}
              search={search}
            >
              {content}
            </ConsoleRuntimeProviders>
          );
        })()
      ) : (
        <AevatarPageLoading fullscreen />
      ),
    ...initialState?.settings,
    title: '',
    menu: {
      ...(initialState?.settings.menu as Record<string, unknown> | undefined),
      collapsedWidth: 40,
      collapsedShowGroupTitle: false,
      collapsedShowTitle: false,
      type: 'group',
    },
    contentStyle: workflowActivityVNextRoute
      ? {
          background: '#ffffff',
          display: 'block',
          height: 'auto',
          inset: 0,
          minHeight: 0,
          overflow: 'hidden',
          padding: 0,
          position: 'fixed',
          width: '100%',
        }
      : fullscreenDisplayRoute
        ? {
            background: '#09110f',
            display: 'block',
            height: '100vh',
            minHeight: 0,
            overflow: 'hidden',
            padding: 0,
          }
        : {
            background: 'transparent',
            display: 'flex',
            flexDirection: 'column',
            height: 'calc(100vh - 56px)',
            minHeight: 0,
            overflow: 'hidden',
            padding: 0,
          },
    defaultCollapsed: shouldDefaultCollapseLayout(pathname, search),
    headerRender: fullscreenDisplayRoute ? false : undefined,
    ...(collapseForRoute ? { collapsed: true } : {}),
    logo: <BrandLogo />,
  };
};

/**
 * @name request config
 * Centralizes network request error handling through the Umi request plugin.
 * @doc https://umijs.org/docs/max/request#config
 */
export const request: Record<string, unknown> = {
  ...errorConfig,
};
