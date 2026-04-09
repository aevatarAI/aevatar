import { useCallback, useEffect, useRef, useState } from 'react';
import * as api from '../api';
import type { ManifestEntry } from './types';
import { detectMediaKind, getMimeType, type MediaFileKind } from './contentFormatting';

export type ConfigStore = ReturnType<typeof useConfigStore>;

export type MediaInfo = {
  mediaKind: MediaFileKind;
  blobUrl: string;
  mimeType: string;
};

export function useConfigStore(_scopeId: string) {
  const [loading, setLoading] = useState(true);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [selectedContent, setSelectedContent] = useState<string | null>(null);
  const [mediaInfo, setMediaInfo] = useState<MediaInfo | null>(null);
  const [contentLoading, setContentLoading] = useState(false);
  const blobUrlRef = useRef<string | null>(null);

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
    // Revoke previous blob URL
    if (blobUrlRef.current) {
      URL.revokeObjectURL(blobUrlRef.current);
      blobUrlRef.current = null;
    }

    if (!selectedKey) {
      setSelectedContent(null);
      setMediaInfo(null);
      return;
    }

    const mediaKind = detectMediaKind(selectedKey);

    if (mediaKind && mediaKind !== 'markdown') {
      // Binary media file — fetch as blob
      setContentLoading(true);
      setSelectedContent(null);
      api.explorer.getFileBlob(selectedKey)
        .then(blob => {
          const url = URL.createObjectURL(blob);
          blobUrlRef.current = url;
          setMediaInfo({ mediaKind, blobUrl: url, mimeType: getMimeType(selectedKey) });
        })
        .catch(() => {
          setMediaInfo(null);
          setSelectedContent(null);
        })
        .finally(() => setContentLoading(false));
    } else {
      // Text-based file (including markdown)
      setMediaInfo(null);
      setContentLoading(true);
      api.explorer.getFile(selectedKey)
        .then(text => setSelectedContent(text))
        .catch(() => setSelectedContent(null))
        .finally(() => setContentLoading(false));
    }
  }, [selectedKey]);

  // Cleanup blob URL on unmount
  useEffect(() => {
    return () => {
      if (blobUrlRef.current) {
        URL.revokeObjectURL(blobUrlRef.current);
      }
    };
  }, []);

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
      setMediaInfo(null);
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
    mediaInfo,
    contentLoading,
    loadManifest,
    saveFile,
    deleteFile,
  };
}
