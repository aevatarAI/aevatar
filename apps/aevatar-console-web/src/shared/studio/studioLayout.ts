export const STUDIO_HOST_BODY_CLASS = "aevatar-studio-host";

export function syncStudioHostBodyClass(enabled: boolean): () => void {
  if (typeof document === "undefined") {
    return () => {};
  }

  document.body.classList.toggle(STUDIO_HOST_BODY_CLASS, enabled);

  return () => {
    if (!enabled) {
      return;
    }

    document.body.classList.remove(STUDIO_HOST_BODY_CLASS);
  };
}
