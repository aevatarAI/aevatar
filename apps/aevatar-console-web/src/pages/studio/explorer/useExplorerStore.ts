import { useQuery, useQueryClient } from "@tanstack/react-query";
import React from "react";
import {
  explorerApi,
  type ExplorerManifestEntry,
} from "@/shared/api/explorerApi";

type ExplorerStore = {
  loading: boolean;
  manifest: ExplorerManifestEntry[];
  errorMessage: string | null;
  selectedKey: string | null;
  setSelectedKey: React.Dispatch<React.SetStateAction<string | null>>;
  selectedEntry: ExplorerManifestEntry | null;
  selectedContent: string | null;
  contentLoading: boolean;
  contentErrorMessage: string | null;
  reloadManifest: () => Promise<void>;
  saveFile: (key: string, content: string) => Promise<void>;
  deleteFile: (key: string) => Promise<void>;
};

export function useExplorerStore(scopeId: string): ExplorerStore {
  const queryClient = useQueryClient();
  const [selectedKey, setSelectedKey] = React.useState<string | null>(null);

  const manifestQuery = useQuery({
    queryKey: ["studio", "explorer", scopeId, "manifest"],
    enabled: Boolean(scopeId),
    queryFn: () => explorerApi.getManifest(),
  });

  const manifest = React.useMemo(
    () => manifestQuery.data?.files ?? [],
    [manifestQuery.data?.files]
  );

  React.useEffect(() => {
    if (!scopeId) {
      setSelectedKey(null);
    }
  }, [scopeId]);

  React.useEffect(() => {
    if (!scopeId) {
      return;
    }

    if (manifest.length === 0) {
      setSelectedKey(null);
      return;
    }

    if (!selectedKey || !manifest.some((entry) => entry.key === selectedKey)) {
      setSelectedKey(manifest[0]?.key ?? null);
    }
  }, [manifest, scopeId, selectedKey]);

  const selectedEntry = React.useMemo(
    () => manifest.find((entry) => entry.key === selectedKey) ?? null,
    [manifest, selectedKey]
  );

  const fileQuery = useQuery({
    queryKey: ["studio", "explorer", scopeId, "file", selectedKey],
    enabled: Boolean(scopeId && selectedKey),
    queryFn: () => explorerApi.getFile(selectedKey!),
  });

  const reloadManifest = React.useCallback(async () => {
    await manifestQuery.refetch();
  }, [manifestQuery]);

  const saveFile = React.useCallback(
    async (key: string, content: string) => {
      await explorerApi.putFile(key, content);
      queryClient.setQueryData(["studio", "explorer", scopeId, "file", key], content);
      await manifestQuery.refetch();
    },
    [manifestQuery, queryClient, scopeId]
  );

  const deleteFile = React.useCallback(
    async (key: string) => {
      await explorerApi.deleteFile(key);
      queryClient.removeQueries({
        queryKey: ["studio", "explorer", scopeId, "file", key],
      });
      if (selectedKey === key) {
        setSelectedKey(null);
      }
      await manifestQuery.refetch();
    },
    [manifestQuery, queryClient, scopeId, selectedKey]
  );

  return {
    loading: manifestQuery.isLoading,
    manifest,
    errorMessage:
      manifestQuery.error instanceof Error ? manifestQuery.error.message : null,
    selectedKey,
    setSelectedKey,
    selectedEntry,
    selectedContent: fileQuery.data ?? null,
    contentLoading: fileQuery.isLoading || fileQuery.isFetching,
    contentErrorMessage:
      fileQuery.error instanceof Error ? fileQuery.error.message : null,
    reloadManifest,
    saveFile,
    deleteFile,
  };
}
