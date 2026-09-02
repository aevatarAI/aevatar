import { CloseOutlined, SearchOutlined } from '@ant-design/icons';
import { Button, Empty, Input, Space, Tag, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import {
  formatStudioStepTypeLabel,
  getStudioGraphCategory,
  STUDIO_GRAPH_CATEGORIES,
} from '@/shared/studio/graph';

type WorkflowStudioNodeLibraryProps = {
  readonly onClose: () => void;
  readonly onInsertNode: (stepType: string) => void;
  readonly open: boolean;
};

const WorkflowStudioNodeLibrary: React.FC<WorkflowStudioNodeLibraryProps> = ({
  onClose,
  onInsertNode,
  open,
}) => {
  const [query, setQuery] = React.useState('');
  const normalizedQuery = query.trim().toLowerCase();
  const filteredCategories = React.useMemo(
    () =>
      STUDIO_GRAPH_CATEGORIES.map((category) => ({
        ...category,
        items: category.items.filter((stepType) => {
          if (!normalizedQuery) {
            return true;
          }

          return (
            stepType.toLowerCase().includes(normalizedQuery) ||
            formatStudioStepTypeLabel(stepType)
              .toLowerCase()
              .includes(normalizedQuery) ||
            category.label.toLowerCase().includes(normalizedQuery)
          );
        }),
      })).filter((category) => category.items.length > 0),
    [normalizedQuery],
  );

  if (!open) {
    return null;
  }

  return (
    <div
      data-testid="node-library-layer"
      style={{
        background: 'rgba(17, 24, 39, 0.16)',
        bottom: 0,
        left: 0,
        position: 'absolute',
        right: 0,
        top: 0,
        zIndex: 30,
      }}
    >
      <button
        aria-label={t(
          'teamMemberWorkflowStudio.nodeLibrary.closeAria',
          'Close node library',
        )}
        onClick={onClose}
        style={{
          background: 'transparent',
          border: 0,
          bottom: 0,
          cursor: 'default',
          left: 0,
          position: 'absolute',
          right: 0,
          top: 0,
        }}
        type="button"
      />
      <aside
        aria-label={t(
          'teamMemberWorkflowStudio.nodeLibrary.sectionAria',
          'Node library',
        )}
        style={{
          background: '#ffffff',
          borderRight: '1px solid #e5e7eb',
          bottom: 0,
          boxShadow: '10px 0 30px rgba(15, 23, 42, 0.08)',
          display: 'flex',
          flexDirection: 'column',
          left: 0,
          maxWidth: 'calc(100% - 48px)',
          overflow: 'hidden',
          position: 'absolute',
          top: 0,
          width: 380,
        }}
      >
        <header
          style={{
            alignItems: 'flex-start',
            borderBottom: '1px solid #eef2f7',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
            padding: '16px 20px 14px',
          }}
        >
          <div style={{ display: 'grid', gap: 4, minWidth: 0 }}>
            <Typography.Text strong style={{ color: '#111827', fontSize: 16 }}>
              {t('teamMemberWorkflowStudio.nodeLibrary.title', 'Node library')}
            </Typography.Text>
          </div>
          <Button
            aria-label={t(
              'teamMemberWorkflowStudio.nodeLibrary.closeAria',
              'Close node library',
            )}
            icon={<CloseOutlined />}
            onClick={onClose}
            size="small"
            style={{ height: 28, width: 28 }}
            type="text"
          />
        </header>
        <Space
          orientation="vertical"
          size={16}
          style={{
            flex: 1,
            minHeight: 0,
            overflow: 'auto',
            padding: '18px 20px 20px',
            width: '100%',
          }}
        >
          <Input
            allowClear
            aria-label={t(
              'teamMemberWorkflowStudio.nodeLibrary.searchAria',
              'Search nodes',
            )}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={t(
              'teamMemberWorkflowStudio.nodeLibrary.searchPlaceholder',
              'Search nodes',
            )}
            prefix={<SearchOutlined />}
            value={query}
          />
          {filteredCategories.length === 0 ? (
            <Empty
              description={t(
                'teamMemberWorkflowStudio.nodeLibrary.emptySearch',
                'No nodes match this search.',
              )}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ) : (
            filteredCategories.map((category) => (
              <section key={category.key}>
                <Space
                  align="center"
                  style={{ justifyContent: 'space-between', width: '100%' }}
                >
                  <Typography.Text strong>{category.label}</Typography.Text>
                  <Tag color={category.color}>{category.items.length}</Tag>
                </Space>
                <div
                  style={{
                    display: 'grid',
                    gap: 8,
                    marginTop: 10,
                  }}
                >
                  {category.items.map((stepType) => {
                    const itemCategory = getStudioGraphCategory(stepType);
                    const stepTypeLabel = formatStudioStepTypeLabel(stepType);
                    return (
                      <button
                        aria-label={t(
                          'teamMemberWorkflowStudio.nodeLibrary.insertNodeAria',
                          'Insert {nodeName} node',
                          { nodeName: stepTypeLabel },
                        )}
                        key={stepType}
                        onClick={() => onInsertNode(stepType)}
                        style={{
                          alignItems: 'center',
                          background: '#ffffff',
                          border: '1px solid #e5e7eb',
                          borderRadius: 8,
                          cursor: 'pointer',
                          display: 'flex',
                          gap: 10,
                          padding: '10px 12px',
                          textAlign: 'left',
                        }}
                        type="button"
                      >
                        <span
                          aria-hidden
                          style={{
                            background: itemCategory.color,
                            borderRadius: 999,
                            display: 'inline-block',
                            height: 10,
                            width: 10,
                          }}
                        />
                        <span style={{ display: 'grid', gap: 2 }}>
                          <Typography.Text strong>
                            {stepTypeLabel}
                          </Typography.Text>
                          <Typography.Text
                            style={{ color: '#6b7280', fontSize: 12 }}
                          >
                            {itemCategory.label}
                          </Typography.Text>
                        </span>
                      </button>
                    );
                  })}
                </div>
              </section>
            ))
          )}
        </Space>
      </aside>
    </div>
  );
};

export default WorkflowStudioNodeLibrary;
