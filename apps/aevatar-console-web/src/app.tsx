import {
  enUSIntl,
  PageLoading,
  ProConfigProvider,
} from "@ant-design/pro-components";
import {
  DownOutlined,
  LogoutOutlined,
  SettingOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { QueryClientProvider } from "@tanstack/react-query";
import { Avatar, Badge, ConfigProvider, Dropdown, Typography } from "antd";
import enUS from "antd/locale/en_US";
import React from "react";
import MainLayout from "@/layouts/MainLayout";
import { history } from "./shared/navigation/history";
import { CONSOLE_HOME_ROUTE } from "@/shared/navigation/consoleHome";
import BrandLogo from "@/components/BrandLogo";
import defaultSettings from "../config/defaultSettings";
import { errorConfig } from "./requestErrorConfig";
import {
  ensureActiveAuthSession,
  hasRestorableAuthSession,
} from "./shared/auth/client";
import { getNyxIDRuntimeConfig } from "./shared/auth/config";
import {
  buildAuthInitialState,
  clearStoredAuthSession,
  loadRestorableAuthSession,
  loadStoredAuthSession,
  sanitizeReturnTo,
} from "./shared/auth/session";
import { ProtectedRouteRedirectGate } from "./shared/auth/ProtectedRouteRedirectGate";
import {
  getNavigationGroupOrder,
  type NavigationGroup,
} from "./shared/navigation/navigationGroups";
import { getNavigationSelectedKeys } from "./shared/navigation/navigationMenuSelection";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { buildMissionSnapshotFromRuntime } from "@/pages/MissionControl/runtimeAdapter";
import { readMissionControlRouteContext } from "@/pages/MissionControl/services/api";
import { loadRecentRuns } from "@/shared/runs/recentRuns";
import { queryClient } from "./shared/query/queryClient";
import { aevatarThemeConfig } from "@/shared/ui/aevatarWorkbench";

const PUBLIC_ROUTES = new Set(["/login", "/auth/callback"]);
const DEFAULT_PROTECTED_ROUTE = CONSOLE_HOME_ROUTE;
const STUDIO_HOST_ROUTES = new Set(["/studio"]);

function isStudioHostRoute(pathname: string): boolean {
  return STUDIO_HOST_ROUTES.has(pathname);
}

function buildLoginRoute(returnTo: string): string {
  const params = new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  });
  return `/login?${params.toString()}`;
}

function getCurrentReturnTo(pathname: string): string {
  return pathname === "/"
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

type NavigationMenuItem = {
  children?: NavigationMenuItem[];
  className?: string;
  disabled?: boolean;
  icon?: React.ReactNode;
  menuBadgeKey?: string;
  menuGroupKey?: string;
  name?: React.ReactNode;
  path?: string;
  key?: React.Key;
  [key: string]: unknown;
};

type AuthSessionBootstrapProps = {
  pathname: string;
  children: React.ReactNode;
};

const LIVE_OPS_ATTENTION_BADGE_KEY = "live.attention";
const LIVE_OPS_ATTENTION_MAX_CANDIDATES = 6;
const LIVE_OPS_ATTENTION_MAX_AGE_MS = 12 * 60 * 60 * 1000;
const LIVE_OPS_ATTENTION_REFRESH_MS = 30_000;
const NAVIGATION_GROUP_ORDER: readonly NavigationGroup[] = getNavigationGroupOrder();
const LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT: LiveOpsAttentionSnapshot = {
  hasPendingAttention: false,
  pendingCount: 0,
};
const liveOpsAttentionListeners = new Set<() => void>();
let liveOpsAttentionSnapshot = LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT;

const navigationGroupLabelStyle: React.CSSProperties = {
  color: "#667085",
  display: "inline-flex",
  fontSize: 14,
  fontWeight: 700,
  lineHeight: "22px",
};

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
  liveOpsAttentionListeners.forEach((listener) => listener());
}

function buildLiveOpsAttentionCandidateKey(
  candidate: LiveOpsAttentionCandidate
): string {
  const actorId = trimOptional(candidate.actorId);
  if (actorId) {
    return `actor:${actorId}`;
  }

  return [
    "run",
    trimOptional(candidate.scopeId) || "",
    trimOptional(candidate.serviceId) || "",
    trimOptional(candidate.runId) || "",
  ].join(":");
}

function collectLiveOpsAttentionCandidates(
  pathname: string,
  search: string
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

    if (entry.status === "finished" || entry.status === "error") {
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

  if (pathname === "/runtime/mission-control") {
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

  return Array.from(deduped.values()).slice(0, LIVE_OPS_ATTENTION_MAX_CANDIDATES);
}

async function resolveLiveOpsAttentionActorId(
  candidate: LiveOpsAttentionCandidate
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
  candidate: LiveOpsAttentionCandidate
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
        direction: "Both",
        take: 120,
      }),
      runtimeActorsApi.getActorTimeline(actorId, {
        take: 120,
      }),
    ]);

    const snapshot = buildMissionSnapshotFromRuntime({
      connectionStatus: "degraded",
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
          status: "running",
        },
      },
    });

    return (
      snapshot.intervention?.required === true &&
      (snapshot.intervention.kind === "human_approval" ||
        snapshot.intervention.kind === "human_input")
    );
  } catch {
    return false;
  }
}

async function loadLiveOpsAttentionSnapshot(
  pathname: string,
  search: string
): Promise<LiveOpsAttentionSnapshot> {
  const candidates = collectLiveOpsAttentionCandidates(pathname, search);
  if (candidates.length === 0) {
    return LIVE_OPS_DEFAULT_ATTENTION_SNAPSHOT;
  }

  const results = await Promise.allSettled(
    candidates.map((candidate) => runNeedsLiveOpsAttention(candidate))
  );
  const pendingCount = results.reduce((count, result) => {
    if (result.status === "fulfilled" && result.value) {
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
    getLiveOpsAttentionSnapshot
  );
  const showCountBadge =
    badgeKey === LIVE_OPS_ATTENTION_BADGE_KEY && snapshot.pendingCount > 0;

  return (
    <span
      style={{
        alignItems: "center",
        display: "inline-flex",
        gap: 8,
        justifyContent: "space-between",
        minWidth: 0,
        width: "100%",
      }}
    >
      <span
        style={{
          alignItems: "center",
          display: "inline-flex",
          gap: 8,
          minWidth: 0,
        }}
      >
        <span
          style={{
            minWidth: 0,
            overflow: "hidden",
            textOverflow: "ellipsis",
            whiteSpace: "nowrap",
          }}
        >
          {label}
        </span>
        {showLiveOpsDot && snapshot.hasPendingAttention ? (
          <span
            aria-hidden="true"
            style={{
              background: "#ef4444",
              borderRadius: 999,
              display: "inline-block",
              flex: "0 0 auto",
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
            backgroundColor: "#ef4444",
            boxShadow: "none",
          }}
        />
      ) : null}
    </span>
  );
});

NavigationMenuLabel.displayName = "NavigationMenuLabel";

const LiveOpsGroupIcon: React.FC<{
  icon: React.ReactNode;
}> = React.memo(({ icon }) => {
  const snapshot = React.useSyncExternalStore(
    subscribeLiveOpsAttention,
    getLiveOpsAttentionSnapshot,
    getLiveOpsAttentionSnapshot
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

LiveOpsGroupIcon.displayName = "LiveOpsGroupIcon";

function groupNavigationMenuItems(items: NavigationMenuItem[]): NavigationMenuItem[] {
  const grouped = new Map<string, NavigationMenuItem[]>();
  const ungrouped: NavigationMenuItem[] = [];

  for (const item of items) {
    const groupKey =
      typeof item.menuGroupKey === "string" ? item.menuGroupKey : undefined;
    if (!groupKey) {
      ungrouped.push(item);
      continue;
    }

    const existing = grouped.get(groupKey);
    if (existing) {
      existing.push(item);
      continue;
    }

    grouped.set(groupKey, [item]);
  }

  const menuGroups = NAVIGATION_GROUP_ORDER.reduce<NavigationMenuItem[]>(
    (result, group) => {
      const children = grouped.get(group.key);
      if (!children || children.length === 0) {
        return result;
      }

      if (group.flattenSingleItem && children.length === 1) {
        result.push({
          ...children[0],
          icon: children[0].icon ?? group.icon,
          menuGroupKey: group.key,
        });
        return result;
      }

      result.push({
        children: children.map((child) => ({
          ...child,
          menuGroupKey: group.key,
        })),
        key: `menu-group:${group.key}`,
        menuGroupKey: group.key,
        name: React.createElement(
          "span",
          {
            style: navigationGroupLabelStyle,
          },
          group.label,
        ),
      });
      return result;
    },
    []
  );

  return [...menuGroups, ...ungrouped];
}

function decorateNavigationMenuItems(
  items: NavigationMenuItem[],
  groupItems = true
): NavigationMenuItem[] {
  const sourceItems = groupItems ? groupNavigationMenuItems(items) : items;

  return sourceItems.map((item) => {
    const path = typeof item.path === "string" ? item.path : undefined;
    const badgeKey =
      typeof item.menuBadgeKey === "string" ? item.menuBadgeKey : undefined;
    const groupKey =
      typeof item.menuGroupKey === "string" ? item.menuGroupKey : undefined;
    const children = Array.isArray(item.children)
      ? decorateNavigationMenuItems(item.children, false)
      : undefined;
    const isLiveOpsGroup =
      groupKey === "live" && Array.isArray(children) && children.length > 0;
    const hasRenderableIcon = React.isValidElement(item.icon);
    const name =
      badgeKey || isLiveOpsGroup
        ? React.createElement(NavigationMenuLabel, {
            badgeKey,
            label: item.name,
            showLiveOpsDot: isLiveOpsGroup && !hasRenderableIcon,
          })
        : item.name;
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
      if (document.visibilityState === "visible") {
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
    document.addEventListener("visibilitychange", refreshWhenVisible);
    window.addEventListener("focus", refreshOnFocus);
    window.addEventListener("storage", refreshOnFocus);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
      document.removeEventListener("visibilitychange", refreshWhenVisible);
      window.removeEventListener("focus", refreshOnFocus);
      window.removeEventListener("storage", refreshOnFocus);
    };
  }, [enabled, pathname, search]);

  return null;
};

const AuthSessionBootstrap: React.FC<AuthSessionBootstrapProps> = ({
  pathname,
  children,
}) => {
  const [ready, setReady] = React.useState(() =>
    Boolean(loadStoredAuthSession())
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
    return <PageLoading fullscreen />;
  }

  return <>{children}</>;
};
// ProLayout 支持的api https://procomponents.ant.design/components/layout
export const layout = ({
  initialState,
}: LayoutRuntimeProps): Record<string, unknown> => {
  return {
    onPageChange: () => {
      const pathname = window.location.pathname;
      if (PUBLIC_ROUTES.has(pathname)) {
        return;
      }

      if (isStudioHostRoute(pathname)) {
        return;
      }

      if (pathname === "/") {
        history.replace(DEFAULT_PROTECTED_ROUTE);
      }
    },
    postMenuData: (menuData: NavigationMenuItem[]) =>
      decorateNavigationMenuItems(menuData),
    menuRender: (_: unknown, defaultDom: React.ReactNode) => {
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
      const session = loadRestorableAuthSession();
      if (!session) {
        return [];
      }

      const displayName =
        session.user.name || session.user.email || session.user.sub;

      return [
        <Dropdown
          key="auth-actions"
          menu={{
            items: [
              {
                key: "settings",
                icon: <SettingOutlined />,
                label: "Settings",
              },
              {
                key: "logout",
                icon: <LogoutOutlined />,
                label: "Logout",
              },
            ],
            onClick: ({ key }) => {
              if (key === "settings") {
                history.push("/settings");
                return;
              }

              if (key === "logout") {
                clearStoredAuthSession();
                window.location.replace("/login");
              }
            },
          }}
          placement="bottomRight"
          trigger={["click"]}
        >
          <span
            style={{
              alignItems: "center",
              background: "var(--ant-color-fill-tertiary)",
              border: "1px solid var(--ant-color-border-secondary)",
              borderRadius: 999,
              cursor: "pointer",
              display: "inline-flex",
              gap: 8,
              height: 36,
              maxWidth: 220,
              padding: "0 10px 0 6px",
            }}
            title={displayName}
          >
            <Avatar
              icon={<UserOutlined />}
              size={24}
              src={session.user.picture}
            />
            <Typography.Text
              style={{
                flex: 1,
                color: "var(--ant-color-text)",
                lineHeight: "20px",
                marginBottom: 0,
                maxWidth: 160,
                minWidth: 0,
                whiteSpace: "nowrap",
              }}
              ellipsis={{ tooltip: displayName }}
            >
              {displayName}
            </Typography.Text>
            <DownOutlined
              style={{
                color: "var(--ant-color-text-tertiary)",
                fontSize: 11,
              }}
            />
          </span>
        </Dropdown>,
      ];
    },
    childrenRender: (children: React.ReactNode) =>
      initialState ? (
        (() => {
          const pathname = window.location.pathname;
          const search = window.location.search;
          const isPublicRoute = PUBLIC_ROUTES.has(pathname);
          const isStudioRoute = isStudioHostRoute(pathname);
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
            <ConfigProvider locale={enUS} theme={aevatarThemeConfig}>
              <ProConfigProvider intl={enUSIntl}>
                <QueryClientProvider client={queryClient}>
                  <LiveOpsAttentionBridge
                    enabled={!isPublicRoute && !isStudioRoute}
                    pathname={pathname}
                    search={search}
                  />
                  {isPublicRoute ? content : <MainLayout>{content}</MainLayout>}
                </QueryClientProvider>
              </ProConfigProvider>
            </ConfigProvider>
          );
        })()
      ) : (
        <PageLoading fullscreen />
      ),
    ...initialState?.settings,
    menu: {
      ...(initialState?.settings.menu as Record<string, unknown> | undefined),
      collapsedWidth: 40,
      collapsedShowGroupTitle: false,
      collapsedShowTitle: false,
      type: "group",
    },
    contentStyle: {
      background: "transparent",
      display: "flex",
      flexDirection: "column",
      height: "calc(100vh - 56px)",
      minHeight: 0,
      overflow: "hidden",
      padding: 0,
    },
    logo: <BrandLogo />,
  };
};

/**
 * @name request 配置，可以配置错误处理
 * 它基于 axios 和 ahooks 的 useRequest 提供了一套统一的网络请求和错误处理方案。
 * @doc https://umijs.org/docs/max/request#配置
 */
export const request: Record<string, unknown> = {
  ...errorConfig,
};
