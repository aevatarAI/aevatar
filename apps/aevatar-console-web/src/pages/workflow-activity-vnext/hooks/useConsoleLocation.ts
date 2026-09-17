import React from 'react';
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';

export function useConsoleLocation(): {
  readonly pathname: string;
  readonly search: string;
} {
  const snapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => '',
  );
  const url = new URL(snapshot || '/', 'http://console.local');
  return { pathname: url.pathname, search: url.search };
}
