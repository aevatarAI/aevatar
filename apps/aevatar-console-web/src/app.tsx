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
import { Avatar, ConfigProvider, Dropdown, Space, Typography } from "antd";
import enUS from "antd/locale/en_US";
import React from "react";
import { history } from "@umijs/max";
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
import { queryClient } from "./shared/query/queryClient";

const PUBLIC_ROUTES = new Set(["/login", "/auth/callback"]);
const DEFAULT_PROTECTED_ROUTE = "/overview";
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

type AuthSessionBootstrapProps = {
  pathname: string;
  children: React.ReactNode;
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

      if (!hasRestorableAuthSession()) {
        history.replace(buildLoginRoute(getCurrentReturnTo(pathname)));
        return;
      }

      if (pathname === "/") {
        history.replace(DEFAULT_PROTECTED_ROUTE);
      }
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
              background: "rgba(0, 0, 0, 0.03)",
              border: "1px solid rgba(5, 5, 5, 0.06)",
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
                lineHeight: "20px",
                marginBottom: 0,
                maxWidth: 160,
                minWidth: 0,
                whiteSpace: "nowrap",
              }}
              ellipsis={{ tooltip: displayName }}
              type="secondary"
            >
              {displayName}
            </Typography.Text>
            <DownOutlined
              style={{
                color: "rgba(0, 0, 0, 0.45)",
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
          const isPublicRoute = PUBLIC_ROUTES.has(pathname);
          const isStudioRoute = isStudioHostRoute(pathname);
          const liveSession = loadStoredAuthSession();
          if (
            !isPublicRoute &&
            !isStudioRoute &&
            !liveSession &&
            !hasRestorableAuthSession()
          ) {
            history.replace(buildLoginRoute(getCurrentReturnTo(pathname)));
            return <PageLoading fullscreen />;
          }

          const content =
            !isPublicRoute && !isStudioRoute && !liveSession ? (
              <AuthSessionBootstrap pathname={pathname}>
                {children}
              </AuthSessionBootstrap>
            ) : (
              children
            );

          return (
            <ConfigProvider locale={enUS}>
              <ProConfigProvider intl={enUSIntl}>
                <QueryClientProvider client={queryClient}>
                  {content}
                </QueryClientProvider>
              </ProConfigProvider>
            </ConfigProvider>
          );
        })()
      ) : (
        <PageLoading fullscreen />
      ),
    ...initialState?.settings,
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
