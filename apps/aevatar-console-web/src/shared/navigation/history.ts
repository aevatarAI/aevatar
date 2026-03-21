type NavigationTarget = string;

function notifyRouteChange(): void {
  window.dispatchEvent(new PopStateEvent('popstate'));
}

function navigate(target: NavigationTarget, replace: boolean): void {
  if (typeof window === 'undefined') {
    return;
  }

  const nextUrl = new URL(target, window.location.origin);
  if (nextUrl.origin !== window.location.origin) {
    if (replace) {
      window.location.replace(nextUrl.toString());
    } else {
      window.location.assign(nextUrl.toString());
    }
    return;
  }

  const nextPath = `${nextUrl.pathname}${nextUrl.search}${nextUrl.hash}`;
  if (replace) {
    window.history.replaceState(window.history.state, '', nextPath);
  } else {
    window.history.pushState(window.history.state, '', nextPath);
  }

  notifyRouteChange();
}

export const history = {
  push(target: NavigationTarget): void {
    navigate(target, false);
  },
  replace(target: NavigationTarget): void {
    navigate(target, true);
  },
};
