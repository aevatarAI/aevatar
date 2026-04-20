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
            title="展开文件列表"
            aria-label="展开文件列表"
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
          <div className="console-scripts-eyebrow">文件</div>
          <div className="console-scripts-package-tree-title">文件列表</div>
        </div>
        <div className="console-scripts-inline-actions">
          <button
            type="button"
            onClick={onToggleCollapsed}
            className="console-scripts-icon-button"
            title="收起文件列表"
            aria-label="收起文件列表"
          >
            <MenuFoldOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('csharp')}
            className="console-scripts-icon-button"
            title="添加 C# 文件"
            aria-label="添加 C# 文件"
          >
            <PlusOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('proto')}
            className="console-scripts-icon-button"
            title="添加 Proto 文件"
            aria-label="添加 Proto 文件"
          >
            <FileTextOutlined />
          </button>
        </div>
      </div>

      <div className="console-scripts-package-tree-body">
        {entries.length === 0 ? (
          <div className="console-scripts-package-tree-empty">
            先添加一个 C# 或 Proto 文件，再开始编写脚本。
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
                      {entry.kind === 'csharp' ? 'C# 源文件' : 'Proto 定义'}
                    </div>
                  </div>
                </button>

                <div className="console-scripts-package-file-footer">
                  <div className="console-scripts-package-file-state">
                    {isEntry ? '入口文件' : '\u00a0'}
                  </div>
                  <div className="console-scripts-inline-actions">
                    {entry.kind === 'csharp' ? (
                      <button
                        type="button"
                        onClick={() => onSetEntry(entry.path)}
                        className={`console-scripts-icon-button ${isEntry ? 'active' : ''}`}
                        title={isEntry ? '入口文件' : '设为入口文件'}
                        aria-label={
                          isEntry ? '入口文件' : `将 ${entry.path} 设为入口文件`
                        }
                      >
                        <StarFilled />
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={() => onRenameFile(entry.path)}
                      className="console-scripts-icon-button"
                      title={`重命名 ${entry.path}`}
                      aria-label={`重命名 ${entry.path}`}
                    >
                      <EditOutlined />
                    </button>
                    <button
                      type="button"
                      onClick={() => onRemoveFile(entry.path)}
                      className="console-scripts-icon-button active"
                      title={`删除 ${entry.path}`}
                      aria-label={`删除 ${entry.path}`}
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
