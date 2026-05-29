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
import { t } from "@/shared/i18n/messages";

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
            title={t("modules.studio.scripts.scriptspackagefiletree.expand.file.list", "Expand file list")}
            aria-label={t("modules.studio.scripts.scriptspackagefiletree.expand.file.list.2", "Expand file list")}
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
          <div className="console-scripts-eyebrow">{t("modules.studio.scripts.scriptspackagefiletree.document", "document")}</div>
          <div className="console-scripts-package-tree-title">{t("modules.studio.scripts.scriptspackagefiletree.file.list", "file list")}</div>
        </div>
        <div className="console-scripts-inline-actions">
          <button
            type="button"
            onClick={onToggleCollapsed}
            className="console-scripts-icon-button"
            title={t("modules.studio.scripts.scriptspackagefiletree.collapse.file.list", "Collapse file list")}
            aria-label={t("modules.studio.scripts.scriptspackagefiletree.collapse.file.list.2", "Collapse file list")}
          >
            <MenuFoldOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('csharp')}
            className="console-scripts-icon-button"
            title={t("modules.studio.scripts.scriptspackagefiletree.add.files", "Add C# files")}
            aria-label={t("modules.studio.scripts.scriptspackagefiletree.add.files.2", "Add C# files")}
          >
            <PlusOutlined />
          </button>
          <button
            type="button"
            onClick={() => onAddFile('proto')}
            className="console-scripts-icon-button"
            title={t("modules.studio.scripts.scriptspackagefiletree.add.proto.file", "Add proto file")}
            aria-label={t("modules.studio.scripts.scriptspackagefiletree.add.proto.file.2", "Add proto file")}
          >
            <FileTextOutlined />
          </button>
        </div>
      </div>

      <div className="console-scripts-package-tree-body">
        {entries.length === 0 ? (
          <div className="console-scripts-package-tree-empty">
            {t("modules.studio.scripts.scriptspackagefiletree.add.or.proto.file", "Add a C# or Proto file before you start scripting your behavior.")}</div>
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
                      {entry.kind === 'csharp' ? t("modules.studio.scripts.scriptspackagefiletree.source.file", "C# source file") : t("modules.studio.scripts.scriptspackagefiletree.proto.definition", "Proto definition")}
                    </div>
                  </div>
                </button>

                <div className="console-scripts-package-file-footer">
                  <div className="console-scripts-package-file-state">
                    {isEntry ? t("modules.studio.scripts.scriptspackagefiletree.entry.file", "Entry file") : '\u00a0'}
                  </div>
                  <div className="console-scripts-inline-actions">
                    {entry.kind === 'csharp' ? (
                      <button
                        type="button"
                        onClick={() => onSetEntry(entry.path)}
                        className={`console-scripts-icon-button ${isEntry ? 'active' : ''}`}
                        title={isEntry ? t("modules.studio.scripts.scriptspackagefiletree.entry.file.2", "Entry file") : t("modules.studio.scripts.scriptspackagefiletree.set.as.entry.file", "Set as entry file")}
                        aria-label={
                          isEntry ? t("modules.studio.scripts.scriptspackagefiletree.entry.file.3", "Entry file") : t("modules.studio.scripts.scriptspackagefiletree.set.as.the.entry", "Set {value1} as the entry file", { value1: entry.path })
                        }
                      >
                        <StarFilled />
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={() => onRenameFile(entry.path)}
                      className="console-scripts-icon-button"
                      title={t("modules.studio.scripts.scriptspackagefiletree.rename", "Rename {value1}", { value1: entry.path })}
                      aria-label={t("modules.studio.scripts.scriptspackagefiletree.rename.2", "Rename {value1}", { value1: entry.path })}
                    >
                      <EditOutlined />
                    </button>
                    <button
                      type="button"
                      onClick={() => onRemoveFile(entry.path)}
                      className="console-scripts-icon-button active"
                      title={t("modules.studio.scripts.scriptspackagefiletree.delete", "Delete {value1}", { value1: entry.path })}
                      aria-label={t("modules.studio.scripts.scriptspackagefiletree.delete.2", "Delete {value1}", { value1: entry.path })}
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
