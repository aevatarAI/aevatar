import {
  ApiOutlined,
  CodeOutlined,
  DownOutlined,
  FileOutlined,
  FolderOpenOutlined,
  MessageOutlined,
  RightOutlined,
  SettingOutlined,
  TeamOutlined,
} from "@ant-design/icons";
import { Typography } from "antd";
import React from "react";
import type { ExplorerManifestEntry } from "@/shared/api/explorerApi";

type ExplorerTreeProps = {
  manifest: ExplorerManifestEntry[];
  onSelect: (key: string) => void;
  scopeId: string;
  search: string;
  selectedKey: string | null;
};

type ExplorerTreeNode =
  | {
      kind: "folder";
      name: string;
      path: string;
      children: ExplorerTreeNode[];
    }
  | {
      kind: "file";
      name: string;
      path: string;
      entry: ExplorerManifestEntry;
    };

const treeScrollStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 4,
  height: 0,
  minHeight: 0,
  overflowX: "hidden",
  overflowY: "auto",
  paddingRight: 4,
};

const treeButtonStyle: React.CSSProperties = {
  alignItems: "center",
  background: "transparent",
  border: "none",
  borderRadius: 10,
  cursor: "pointer",
  display: "flex",
  gap: 10,
  justifyContent: "space-between",
  padding: "10px 12px",
  textAlign: "left",
  width: "100%",
};

const treeButtonActiveStyle: React.CSSProperties = {
  background: "rgba(22, 119, 255, 0.08)",
};

const treeMetaStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
};

function matchesSearch(entry: ExplorerManifestEntry, search: string): boolean {
  const keyword = search.trim().toLowerCase();
  if (!keyword) {
    return true;
  }

  return [entry.key, entry.name, entry.type].some((value) =>
    String(value || "").toLowerCase().includes(keyword)
  );
}

function formatUpdatedAt(updatedAt?: string): string | undefined {
  if (!updatedAt) {
    return undefined;
  }

  const value = Date.parse(updatedAt);
  if (!Number.isFinite(value)) {
    return updatedAt;
  }

  return new Date(value).toLocaleDateString();
}

function createFolderNode(name: string, path: string): Extract<ExplorerTreeNode, { kind: "folder" }> {
  return {
    kind: "folder",
    name,
    path,
    children: [],
  };
}

function insertTreeNode(
  siblings: ExplorerTreeNode[],
  entry: ExplorerManifestEntry
): void {
  const segments = entry.key.split("/").filter(Boolean);
  let current = siblings;

  for (let index = 0; index < segments.length; index += 1) {
    const segment = segments[index];
    const path = segments.slice(0, index + 1).join("/");
    const isLeaf = index === segments.length - 1;

    if (isLeaf) {
      current.push({
        kind: "file",
        name: segment,
        path,
        entry,
      });
      return;
    }

    let folder = current.find(
      (node): node is Extract<ExplorerTreeNode, { kind: "folder" }> =>
        node.kind === "folder" && node.path === path
    );
    if (!folder) {
      folder = createFolderNode(segment, path);
      current.push(folder);
    }

    current = folder.children;
  }
}

function compareTreeNodes(left: ExplorerTreeNode, right: ExplorerTreeNode): number {
  if (left.kind !== right.kind) {
    return left.kind === "folder" ? -1 : 1;
  }

  return left.name.localeCompare(right.name);
}

function sortTree(nodes: ExplorerTreeNode[]): ExplorerTreeNode[] {
  return [...nodes]
    .sort(compareTreeNodes)
    .map((node) =>
      node.kind === "folder"
        ? {
            ...node,
            children: sortTree(node.children),
          }
        : node
    );
}

function buildTree(entries: ExplorerManifestEntry[]): ExplorerTreeNode[] {
  const tree: ExplorerTreeNode[] = [];
  for (const entry of [...entries].sort((left, right) => left.key.localeCompare(right.key))) {
    insertTreeNode(tree, entry);
  }

  return sortTree(tree);
}

function iconForEntry(type: string): React.ReactNode {
  switch (type) {
    case "config":
      return <SettingOutlined />;
    case "roles":
      return <TeamOutlined />;
    case "connectors":
      return <ApiOutlined />;
    case "workflow":
      return <FileOutlined />;
    case "script":
      return <CodeOutlined />;
    case "chat-history":
      return <MessageOutlined />;
    default:
      return <FileOutlined />;
  }
}

const ExplorerTree: React.FC<ExplorerTreeProps> = ({
  manifest,
  onSelect,
  scopeId,
  search,
  selectedKey,
}) => {
  const [openFolders, setOpenFolders] = React.useState<Record<string, boolean>>({});

  const filteredManifest = React.useMemo(
    () => manifest.filter((entry) => matchesSearch(entry, search)),
    [manifest, search]
  );
  const tree = React.useMemo(() => buildTree(filteredManifest), [filteredManifest]);

  const isSearching = search.trim().length > 0;
  const rootLabel = scopeId || "storage";

  const toggleFolder = React.useCallback((path: string) => {
    setOpenFolders((current) => ({
      ...current,
      [path]: !(current[path] ?? true),
    }));
  }, []);

  const renderNode = React.useCallback(
    (node: ExplorerTreeNode, depth = 0): React.ReactNode => {
      if (node.kind === "folder") {
        const open = isSearching ? true : openFolders[node.path] ?? true;
        return (
          <React.Fragment key={node.path}>
            <button
              type="button"
              onClick={() => toggleFolder(node.path)}
              style={{
                ...treeButtonStyle,
                paddingLeft: 12 + depth * 20,
              }}
            >
              <span style={{ alignItems: "center", display: "flex", gap: 10 }}>
                {open ? <DownOutlined /> : <RightOutlined />}
                <FolderOpenOutlined />
                <Typography.Text>{node.name}/</Typography.Text>
              </span>
            </button>
            {open ? node.children.map((child) => renderNode(child, depth + 1)) : null}
          </React.Fragment>
        );
      }

      const entry = node.entry;
      const meta = formatUpdatedAt(entry.updatedAt) || entry.type;

      return (
        <button
          key={entry.key}
          type="button"
          onClick={() => onSelect(entry.key)}
          style={{
            ...treeButtonStyle,
            ...(selectedKey === entry.key ? treeButtonActiveStyle : null),
            paddingLeft: 20 + depth * 20,
          }}
        >
          <span style={{ alignItems: "center", display: "flex", gap: 10, minWidth: 0 }}>
            <span aria-hidden="true">{iconForEntry(entry.type)}</span>
            <span style={{ minWidth: 0 }}>
              <Typography.Text ellipsis>{node.name}</Typography.Text>
              <div>
                <Typography.Text style={treeMetaStyle}>{meta}</Typography.Text>
              </div>
            </span>
          </span>
        </button>
      );
    },
    [isSearching, onSelect, openFolders, selectedKey, toggleFolder]
  );

  return (
    <div style={treeScrollStyle}>
      <div style={{ padding: "4px 12px" }}>
        <Typography.Text strong>{rootLabel}/</Typography.Text>
      </div>

      {!scopeId ? (
        <Typography.Text style={{ ...treeMetaStyle, paddingInline: 12 }}>
          Resolve a project scope to browse explorer storage.
        </Typography.Text>
      ) : filteredManifest.length === 0 ? (
        <Typography.Text style={{ ...treeMetaStyle, paddingInline: 12 }}>
          {manifest.length === 0 ? "No explorer files found." : "No explorer files matched."}
        </Typography.Text>
      ) : (
        tree.map((node) => renderNode(node))
      )}
    </div>
  );
};

export default ExplorerTree;
