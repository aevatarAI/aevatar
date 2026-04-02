import {
  CodeOutlined,
  FileTextOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  PlusOutlined,
  StarFilled,
  DeleteOutlined,
  EditOutlined,
} from '@ant-design/icons';
import React from 'react';
import type { ScriptPackageEntry } from '@/shared/studio/scriptsModels';

type ScriptsPackageFileTreeProps = {
  entries: ScriptPackageEntry[];
  selectedFilePath: string;
  entrySourcePath: string;
  collapsed: boolean;
  onToggleCollapsed: () => void;
  onSelectFile: (filePath: string) => void;
  onAddFile: (kind: 'csharp' | 'proto') => void;
  onRenameFile: (filePath: string) => void;
  onRemoveFile: (filePath: string) => void;
  onSetEntry: (filePath: string) => void;
};

const ScriptsPackageFileTree: React.FC<ScriptsPackageFileTreeProps> = ({
  entries,
  selectedFilePath,
  entrySourcePath,
  collapsed,
  onToggleCollapsed,
  onSelectFile,
  onAddFile,
  onRenameFile,
  onRemoveFile,
  onSetEntry,
}) => {
  if (collapsed) {
    return (
      <div className="console-scripts-package-tree collapsed">
        <div className="console-scripts-package-tree-head collapsed">
          <button
            type="button"
            onClick={onToggleCollapsed}
            className="console-scripts-icon-button"
            title="Expand files"
            aria-label="Expand files"
          >
            <MenuUnfoldOutlined />
          </button>
        </div>
        <div className="console-scripts-package-tree-collapsed-list">
          {entries.length === 0 ? (
            <div className="console-scripts-collapsed-empty">
              <FileTextOutlined />
            </div>
          ) : (
            entries.map((entry) => {
              const active = selectedFilePath === entry.path;
              const isEntry =
                entry.kind === 'csharp' && entrySourcePath === entry.path;
              return (
                <button
                  key={`${entry.kind}:${entry.path}`}
                  type="button"
                  onClick={() => onSelectFile(entry.path)}
                  className={`console-scripts-collapsed-file ${active ? 'active' : ''}`}
                  title={entry.path}
                  aria-label={entry.path}
                >
                  {entry.kind === 'csharp' ? (
                    <CodeOutlined />
                  ) : (
                    <FileTextOutlined />
                  )}
                  {isEntry ? (
                    <span className="console-scripts-collapsed-file-badge">
                      <StarFilled />
                    </span>
                  ) : null}
                </button>
              );
            })
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="console-scripts-package-tree">
      <div className="console-scripts-package-tree-head">
        <div>
          <div className="console-scripts-eyebrow">Package</div>
          <div className="console-scripts-package-tree-title">Files</div>
        </div>
        <div className="console-scripts-inline-actions">
          <button
            type="button"
            onClick={onToggleCollapsed}
            className="console-scripts-icon-button"
            title="Collapse files"
            aria-label="Collapse files"
          >
            <MenuFoldOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('csharp')}
            className="console-scripts-icon-button"
            title="Add C# file"
            aria-label="Add C# file"
          >
            <PlusOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('proto')}
            className="console-scripts-icon-button"
            title="Add proto file"
            aria-label="Add proto file"
          >
            <FileTextOutlined />
          </button>
        </div>
      </div>

      <div className="console-scripts-package-tree-body">
        {entries.length === 0 ? (
          <div className="console-scripts-package-tree-empty">
            Add a C# or proto file to turn this draft into a script package.
          </div>
        ) : (
          entries.map((entry) => {
            const active = selectedFilePath === entry.path;
            const isEntry =
              entry.kind === 'csharp' && entrySourcePath === entry.path;
            return (
              <div
                key={`${entry.kind}:${entry.path}`}
                className={`console-scripts-package-file ${active ? 'active' : ''}`}
              >
                <button
                  type="button"
                  onClick={() => onSelectFile(entry.path)}
                  className="console-scripts-package-file-main"
                >
                  <div
                    className={`console-scripts-package-file-icon ${entry.kind}`}
                  >
                    {entry.kind === 'csharp' ? (
                      <CodeOutlined />
                    ) : (
                      <FileTextOutlined />
                    )}
                  </div>
                  <div className="console-scripts-package-file-copy">
                    <div className="console-scripts-package-file-path">
                      {entry.path}
                    </div>
                    <div className="console-scripts-package-file-kind">
                      {entry.kind === 'csharp' ? 'C# source' : 'Proto schema'}
                    </div>
                  </div>
                </button>

                <div className="console-scripts-package-file-footer">
                  <div className="console-scripts-package-file-state">
                    {isEntry ? 'Entry source' : '\u00a0'}
                  </div>
                  <div className="console-scripts-inline-actions">
                    {entry.kind === 'csharp' ? (
                      <button
                        type="button"
                        onClick={() => onSetEntry(entry.path)}
                        className={`console-scripts-icon-button ${isEntry ? 'active' : ''}`}
                        title={isEntry ? 'Entry source' : 'Use as entry source'}
                        aria-label={
                          isEntry ? 'Entry source' : `Use ${entry.path} as entry source`
                        }
                      >
                        <StarFilled />
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={() => onRenameFile(entry.path)}
                      className="console-scripts-icon-button"
                      title={`Rename ${entry.path}`}
                      aria-label={`Rename ${entry.path}`}
                    >
                      <EditOutlined />
                    </button>
                    <button
                      type="button"
                      onClick={() => onRemoveFile(entry.path)}
                      className="console-scripts-icon-button active"
                      title={`Remove ${entry.path}`}
                      aria-label={`Remove ${entry.path}`}
                    >
                      <DeleteOutlined />
                    </button>
                  </div>
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
};

export default ScriptsPackageFileTree;
