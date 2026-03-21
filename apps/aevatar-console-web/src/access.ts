/**
 * @see https://umijs.org/docs/max/access#access
 * */
type AccessInitialState = {
  auth?: {
    enabled?: boolean;
    isAuthenticated?: boolean;
  };
};

export default function access(
  initialState: AccessInitialState | undefined,
) {
  return {
    canAccessConsole: Boolean(initialState?.auth?.isAuthenticated),
  };
}
