function resolveSameOriginTarget(target: string): string | null {
  if (typeof window === "undefined") {
    return null;
  }

  try {
    const url = new URL(target, window.location.href);
    if (url.origin !== window.location.origin) {
      return null;
    }

    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return target.startsWith("/") ? target : null;
  }
}

function navigate(target: string, replace: boolean): void {
  if (typeof window === "undefined") {
    return;
  }

  const nextTarget = resolveSameOriginTarget(target);
  if (nextTarget == null) {
    if (replace) {
      window.location.replace(target);
      return;
    }

    window.location.assign(target);
    return;
  }

  const currentTarget = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  if (replace && nextTarget === currentTarget) {
    return;
  }

  if (replace) {
    window.history.replaceState(window.history.state, "", nextTarget);
  } else {
    window.history.pushState(window.history.state, "", nextTarget);
  }

  window.dispatchEvent(new PopStateEvent("popstate", { state: window.history.state }));
}

export const history = {
  push(target: string): void {
    navigate(target, false);
  },
  replace(target: string): void {
    navigate(target, true);
  },
};
