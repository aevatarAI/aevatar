import { useCallback, useEffect, useState } from 'react';
import * as api from '../api';
import type { ManifestEntry } from './types';

export type ConfigStore = ReturnType<typeof useConfigStore>;

export function useConfigStore(_scopeId: string) {
  const [loading, setLoading] = useState(true);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [selectedContent, setSelectedContent] = useState<string | null>(null);
  const [contentLoading, setContentLoading] = useState(false);

  const loadManifest = useCallback(async () => {
    setLoading(true);
    try {
      const result = await api.explorer.getManifest();
      setManifest(result.files ?? []);
      setErrorMessage(null);
    } catch (error: any) {
      setManifest([]);
      setErrorMessage(
        api.isChronoStorageServiceError(error)
          ? api.getChronoStorageServiceErrorMessage(error)
          : (error?.message || 'Failed to load explorer files.')
      );
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { loadManifest(); }, [loadManifest]);

  useEffect(() => {
    if (!selectedKey) { setSelectedContent(null); return; }
    setContentLoading(true);
    api.explorer.getFile(selectedKey)
      .then(text => setSelectedContent(text))
      .catch(() => setSelectedContent(null))
      .finally(() => setContentLoading(false));
  }, [selectedKey]);

  const saveFile = useCallback(async (key: string, content: string) => {
    await api.explorer.putFile(key, content);
    setSelectedContent(content);
    await loadManifest();
  }, [loadManifest]);

  const deleteFile = useCallback(async (key: string) => {
    await api.explorer.deleteFile(key);
    if (selectedKey === key) {
      setSelectedKey(null);
      setSelectedContent(null);
    }
    await loadManifest();
  }, [selectedKey, loadManifest]);

  return {
    loading,
    manifest,
    errorMessage,
    selectedKey,
    setSelectedKey,
    selectedContent,
    contentLoading,
    loadManifest,
    saveFile,
    deleteFile,
  };
}
