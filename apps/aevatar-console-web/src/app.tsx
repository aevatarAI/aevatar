import { ProConfigProvider } from '@ant-design/pro-components';
import { QueryClientProvider } from '@tanstack/react-query';
import { getLocale, useIntl } from '@umijs/max';
import { Badge, ConfigProvider } from 'antd';
import React from 'react';
import BrandLogo from '@/components/BrandLogo';
import MainLayout from '@/layouts/MainLayout';
import { buildMissionSnapshotFromRuntime } from '@/pages/MissionControl/runtimeAdapter';
import { readMissionControlRouteContext } from '@/pages/MissionControl/services/api';
import { runtimeActorsApi } from '@/shared/api/runtimeActorsApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import {
  normalizeConsoleLocale,
  resolveAntdLocale,
  resolveProIntl,
} from '@/shared/i18n/localeProvider';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { loadRecentRuns } from '@/shared/runs/recentRuns';
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
  buildAuthInitialState,
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

const PUBLIC_ROUTES = new Set(['/login', '/auth/callback']);
const DEFAULT_PROTECTED_ROUTE = CONSOLE_HOME_ROUTE;
const FULLSCREEN_DISPLAY_ROUTES = new Set(['/runtime/mission-wall']);
const WORKFLOW_ACTIVITY_VNEXT_ROUTE =
  /^\/scopes\/[^/]+\/workflow-activity-vnext(?:\/|$)/;
const STUDIO_HOST_ROUTES = new Set([
  '/studio',
  '/scopes/:scopeId/teams/:teamId/members/new/workflow',
  '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
]);

function isFullscreenDisplayRoute(pathname: string): boolean {
  return (
    FULLSCREEN_DISPLAY_ROUTES.has(pathname) ||
    WORKFLOW_ACTIVITY_VNEXT_ROUTE.test(pathname)
  );
}

function isWorkflowActivityVNextRoute(pathname: string): boolean {
  return WORKFLOW_ACTIVITY_VNEXT_ROUTE.test(pathname);
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

type LiveOpsAttentionSnapshot = {
  hasPendingAttention: boolean;
  pendingCount: number;
};

type LiveOpsAttentionCandidate = {
  actorId?: string;
  runId?: string;
  scopeId?: string;
  serviceId?: string;
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
const LIVE_OPS_ATTENTION_MAX_CANDIDATES = 6;
const LIVE_OPS_ATTENTION_MAX_AGE_MS = 12 * 60 * 60 * 1000;
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

function trimOptional(value?: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

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

function buildLiveOpsAttentionCandidateKey(
  candidate: LiveOpsAttentionCandidate,
): string {
  const actorId = trimOptional(candidate.actorId);
  if (actorId) {
    return `actor:${actorId}`;
  }

  return [
    'run',
    trimOptional(candidate.scopeId) || '',
    trimOptional(candidate.serviceId) || '',
    trimOptional(candidate.runId) || '',
  ].join(':');
}

function collectLiveOpsAttentionCandidates(
  pathname: string,
  search: string,
): LiveOpsAttentionCandidate[] {
  const nowMs = Date.now();
  const deduped = new Map<string, LiveOpsAttentionCandidate>();

  for (const entry of loadRecentRuns()) {
    const recordedAtMs = Date.parse(entry.recordedAt);
    if (
      Number.isFinite(recordedAtMs) &&
      nowMs - recordedAtMs > LIVE_OPS_ATTENTION_MAX_AGE_MS
    ) {
      continue;
    }

    if (entry.status === 'finished' || entry.status === 'error') {
      continue;
    }

    const candidate: LiveOpsAttentionCandidate = {
      actorId: trimOptional(entry.actorId),
      runId: trimOptional(entry.runId),
      scopeId: trimOptional(entry.scopeId),
      serviceId: trimOptional(entry.serviceOverrideId),
    };
    const key = buildLiveOpsAttentionCandidateKey(candidate);
    if (!deduped.has(key)) {
      deduped.set(key, candidate);
    }

    if (deduped.size >= LIVE_OPS_ATTENTION_MAX_CANDIDATES) {
      break;
    }
  }

  if (pathname === '/runtime/mission-control') {
    const context = readMissionControlRouteContext(search);
    const candidate: LiveOpsAttentionCandidate = {
      actorId: trimOptional(context.actorId),
      runId: trimOptional(context.runId),
      scopeId: trimOptional(context.scopeId),
      serviceId: trimOptional(context.serviceId),
    };
    const key = buildLiveOpsAttentionCandidateKey(candidate);
    if (
      (candidate.actorId || (candidate.scopeId && candidate.runId)) &&
      !deduped.has(key)
    ) {
      deduped.set(key, candidate);
    }
  }

  return Array.from(deduped.values()).slice(
    0,
    LIVE_OPS_ATTENTION_MAX_CANDIDATES,
  );
}

async function resolveLiveOpsAttentionActorId(
  candidate: LiveOpsAttentionCandidate,
): Promise<string | undefined> {
  const actorId = trimOptional(candidate.actorId);
  if (actorId) {
    return actorId;
  }

  const scopeId = trimOptional(candidate.scopeId);
  const runId = trimOptional(candidate.runId);
  if (!scopeId || !runId) {
    return undefined;
  }

  try {
    const summary = await runtimeRunsApi.getRunSummary(scopeId, runId, {
      serviceId: trimOptional(candidate.serviceId),
    });
    return trimOptional(summary.actorId);
  } catch {
    return undefined;
  }
}

async function runNeedsLiveOpsAttention(
  candidate: LiveOpsAttentionCandidate,
): Promise<boolean> {
  const actorId = await resolveLiveOpsAttentionActorId(candidate);
  if (!actorId) {
    return false;
  }

  try {
    const fetchedAtMs = Date.now();
    const [graph, timeline] = await Promise.all([
      runtimeActorsApi.getActorGraphEnriched(actorId, {
        depth: 4,
        direction: 'Both',
        take: 120,
      }),
      runtimeActorsApi.getActorTimeline(actorId, {
        take: 120,
      }),
    ]);

    const snapshot = buildMissionSnapshotFromRuntime({
      connectionStatus: 'degraded',
      nowMs: fetchedAtMs,
      recentEvents: [],
      routeContext: {
        actorId,
        runId: trimOptional(candidate.runId),
        scopeId: trimOptional(candidate.scopeId),
        serviceId: trimOptional(candidate.serviceId),
      },
      resources: {
        artifacts: {
          fetchedAtMs,
          graph,
          timeline,
        },
        session: {
          runId: trimOptional(candidate.runId),
          status: 'running',
        },
      },
    });

    return (
      snapshot.intervention?.required === true &&
      (snapshot.intervention.kind === 'human_approval' ||
        snapshot.intervention.kind === 'human_input')
    );
  } catch {
    return false;
  }
}

async function loadLiveOpsAttentionSnapshot(
  pathname: string,
  search: string,
): Promise<LiveOpsAttentionSnapshot> {
  const candidates = collectLiveOpsAttentionCandidates(pathname, search);
  if (candidates.length === 0) {
    return LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT;
  }

  const results = await Promise.allSettled(
    candidates.map((candidate) => runNeedsLiveOpsAttention(candidate)),
  );
  const pendingCount = results.reduce((count, result) => {
    if (result.status === 'fulfilled' && result.value) {
      return count + 1;
    }

    return count;
  }, 0);

  return {
    hasPendingAttention: pendingCount > 0,
    pendingCount,
  };
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
        const snapshot = await loadLiveOpsAttentionSnapshot(pathname, search);
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

    if (loadStoredAuthSession()) {
      setReady(true);
      return undefined;
    }

    setReady(false);
    void ensureActiveAuthSession().then((session) => {
      if (cancelled) {
        return;
      }

      if (!session) {
        history.replace(buildLoginRoute(getCurrentReturnTo(pathname)));
        return;
      }

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
              enabled={!isPublicRoute && !isStudioRoute}
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
          const liveSession = loadStoredAuthSession();
          const needsProtectedRouteRedirect =
            !isPublicRoute &&
            !isStudioRoute &&
            !liveSession &&
            !hasRestorableAuthSession();

          const content = needsProtectedRouteRedirect ? (
            <ProtectedRouteRedirectGate pathname={pathname} />
          ) : !isPublicRoute && !isStudioRoute && !liveSession ? (
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
