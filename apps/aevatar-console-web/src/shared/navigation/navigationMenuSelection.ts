import routes from "../../../config/routes";

type NavigationRoute = {
  hideInMenu?: boolean;
  parentKeys?: string[];
  path?: string;
  redirect?: string;
};

type NavigationRouteMatcher = NavigationRoute & {
  matcher: RegExp;
};

function escapePathSegment(path: string): string {
  return path.replace(/[|\\{}()[\]^$+?.]/g, "\\$&");
}

function createPathMatcher(path: string): RegExp {
  const escapedPath = path
    .split("/")
    .map((segment) => {
      if (!segment) {
        return "";
      }

      if (segment === "*") {
        return ".*";
      }

      if (segment.startsWith(":")) {
        return "[^/]+";
      }

      return escapePathSegment(segment);
    })
    .join("/");

  return new RegExp(`^${escapedPath}$`);
}

const navigationRoutes: NavigationRouteMatcher[] = (routes as NavigationRoute[])
  .filter((route) => typeof route.path === "string")
  .map((route) => ({
    ...route,
    matcher: createPathMatcher(route.path as string),
  }));

function parseNavigationLocation(
  pathnameOrLocation: string,
  search: string,
): { pathname: string; search: string } {
  const parsedLocation = new URL(
    pathnameOrLocation,
    "https://console.aevatar.local",
  );

  return {
    pathname: parsedLocation.pathname,
    search: search || parsedLocation.search,
  };
}

function hasMissionControlRunContext(search: string): boolean {
  return new URLSearchParams(search).has("runId");
}

export function getNavigationSelectedKeys(
  pathnameOrLocation: string,
  search = "",
): string[] {
  const { pathname, search: normalizedSearch } = parseNavigationLocation(
    pathnameOrLocation,
    search,
  );

  if (pathname === "/runtime/mission-control") {
    return hasMissionControlRunContext(normalizedSearch)
      ? ["/runtime/runs"]
      : ["/runtime/explorer"];
  }

  const normalizedPathname = pathname.split("?")[0]?.split("#")[0] ?? pathname;
  const matchedRoute = navigationRoutes.find((route) =>
    route.matcher.test(normalizedPathname),
  );

  if (!matchedRoute) {
    return [];
  }

  if (
    !matchedRoute.hideInMenu &&
    !matchedRoute.redirect &&
    typeof matchedRoute.path === "string"
  ) {
    return [matchedRoute.path];
  }

  return matchedRoute.parentKeys?.length ? [matchedRoute.parentKeys[0]] : [];
}
