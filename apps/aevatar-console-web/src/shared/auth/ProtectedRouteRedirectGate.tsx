import { PageLoading } from "@ant-design/pro-components";
import React from "react";
import { CONSOLE_HOME_ROUTE } from "@/shared/navigation/consoleHome";
import { history } from "@/shared/navigation/history";
import { sanitizeReturnTo } from "./session";

type ProtectedRouteRedirectGateProps = {
  pathname: string;
};

function buildLoginRoute(returnTo: string): string {
  const params = new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  });
  return `/login?${params.toString()}`;
}

function getCurrentReturnTo(pathname: string): string {
  return pathname === "/"
    ? CONSOLE_HOME_ROUTE
    : `${pathname}${window.location.search}${window.location.hash}`;
}

export const ProtectedRouteRedirectGate: React.FC<
  ProtectedRouteRedirectGateProps
> = ({ pathname }) => {
  React.useEffect(() => {
    history.replace(buildLoginRoute(getCurrentReturnTo(pathname)));
  }, [pathname]);

  return <PageLoading fullscreen />;
};
