import { Skeleton, theme } from 'antd';
import React from 'react';

export type AevatarContentSkeletonVariant = 'canvas' | 'list' | 'table';

export type AevatarContentSkeletonProps = {
  readonly ariaLabel: string;
  readonly className?: string;
  readonly columnWidths?: readonly (number | string)[];
  readonly listLayout?: 'grid' | 'stack' | 'tree';
  readonly rows?: number;
  readonly style?: React.CSSProperties;
  readonly tableMinWidth?: number;
  readonly variant: AevatarContentSkeletonVariant;
};

const defaultColumnWidths = [112, '1.5fr', '1fr', 120] as const;

function normalizeCount(value: number | undefined, fallback: number): number {
  if (!Number.isFinite(value)) {
    return fallback;
  }

  return Math.max(1, Math.floor(value ?? fallback));
}

function toGridTrack(value: number | string): string {
  return typeof value === 'number' ? `${value}px` : value;
}

export const AevatarContentSkeleton: React.FC<AevatarContentSkeletonProps> = ({
  ariaLabel,
  className,
  columnWidths = defaultColumnWidths,
  listLayout = 'stack',
  rows = 4,
  style,
  tableMinWidth,
  variant,
}) => {
  const { token } = theme.useToken();
  const normalizedRows = normalizeCount(rows, 4);
  const normalizedColumns =
    columnWidths.length > 0 ? columnWidths : defaultColumnWidths;
  const skeletonRows = Array.from({ length: normalizedRows }, (_, index) => ({
    isLast: index === normalizedRows - 1,
    key: `row-${index + 1}`,
  }));
  const skeletonColumns = normalizedColumns.map((width, index) => ({
    isLeading: index === 0,
    isWide: index % 2 !== 0,
    key: `column-${index + 1}-${toGridTrack(width)}`,
    width,
  }));
  const tableGridStyle: React.CSSProperties = {
    alignItems: 'center',
    display: 'grid',
    gap: 16,
    gridTemplateColumns: skeletonColumns
      .map(({ width }) => toGridTrack(width))
      .join(' '),
    minWidth: Math.max(
      640,
      skeletonColumns.length * 136,
      Number.isFinite(tableMinWidth) ? (tableMinWidth ?? 0) : 0,
    ),
  };

  const tablePreset =
    variant === 'table' ? (
      <div
        style={{
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadiusLG,
          overflowX: 'auto',
        }}
      >
        <div
          style={{
            ...tableGridStyle,
            background: token.colorFillQuaternary,
            borderBottom: `1px solid ${token.colorBorderSecondary}`,
            padding: '12px 16px',
          }}
        >
          {skeletonColumns.map((column) => (
            <Skeleton.Input
              active
              key={`header-${column.key}`}
              size="small"
              style={{ height: 12, maxWidth: '100%', width: '72%' }}
            />
          ))}
        </div>
        {skeletonRows.map((row) => (
          <div
            data-testid="aevatar-content-skeleton-row"
            key={row.key}
            style={{
              ...tableGridStyle,
              borderBottom: row.isLast
                ? undefined
                : `1px solid ${token.colorBorderSecondary}`,
              minHeight: 56,
              padding: '12px 16px',
            }}
          >
            {skeletonColumns.map((column) => (
              <div
                data-testid="aevatar-content-skeleton-cell"
                key={`${row.key}-${column.key}`}
              >
                <Skeleton.Input
                  active
                  size="small"
                  style={{
                    height: column.isLeading ? 20 : 14,
                    maxWidth: '100%',
                    width: column.isWide ? '84%' : '68%',
                  }}
                />
              </div>
            ))}
          </div>
        ))}
      </div>
    ) : null;

  const listContainerStyle: React.CSSProperties =
    listLayout === 'grid'
      ? {
          display: 'grid',
          gap: 12,
          gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
        }
      : {
          display: 'flex',
          flexDirection: 'column',
          gap: listLayout === 'tree' ? 4 : 10,
        };
  const listPreset =
    variant === 'list' ? (
      <div style={listContainerStyle}>
        {skeletonRows.map((row) => {
          const isTree = listLayout === 'tree';

          return (
            <div
              className={`aevatar-content-skeleton-list-item aevatar-content-skeleton-list-item-${listLayout}`}
              data-testid="aevatar-content-skeleton-list-item"
              key={`list-item-${row.key}`}
              style={{
                alignItems: 'center',
                border: isTree
                  ? undefined
                  : `1px solid ${token.colorBorderSecondary}`,
                borderRadius: isTree ? 0 : token.borderRadiusLG,
                display: 'grid',
                gap: isTree ? 10 : 14,
                gridTemplateColumns: isTree
                  ? '16px minmax(0, 1fr)'
                  : '36px minmax(0, 1fr) auto',
                minHeight: isTree ? 34 : listLayout === 'grid' ? 112 : 72,
                padding: isTree ? '5px 12px 5px 32px' : 14,
              }}
            >
              <Skeleton.Avatar
                active
                shape={isTree ? 'square' : 'circle'}
                size={isTree ? 16 : 36}
              />
              <div
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 8,
                  minWidth: 0,
                }}
              >
                <Skeleton.Input
                  active
                  size="small"
                  style={{ height: 16, maxWidth: '100%', width: '68%' }}
                />
                {!isTree ? (
                  <Skeleton.Input
                    active
                    size="small"
                    style={{ height: 12, maxWidth: '100%', width: '86%' }}
                  />
                ) : null}
              </div>
              {!isTree ? (
                <Skeleton.Button
                  active
                  shape="round"
                  size="small"
                  style={{ width: 72 }}
                />
              ) : null}
            </div>
          );
        })}
      </div>
    ) : null;

  const canvasPreset =
    variant === 'canvas' ? (
      <div
        className="aevatar-content-skeleton-canvas"
        style={{ display: 'flex', flexDirection: 'column', gap: 14 }}
      >
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Skeleton.Input
            active
            size="small"
            style={{ height: 18, maxWidth: '46%', width: 240 }}
          />
          <Skeleton.Button active shape="round" size="small" />
        </div>
        <div
          className="aevatar-content-skeleton-canvas-surface"
          style={{
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: token.borderRadiusLG,
            minHeight: 280,
            overflow: 'hidden',
            position: 'relative',
          }}
        >
          <div
            className="aevatar-content-skeleton-connector"
            data-testid="aevatar-content-skeleton-connector"
            style={{
              background: token.colorBorderSecondary,
              height: 2,
              left: '22%',
              position: 'absolute',
              top: '48%',
              width: '56%',
            }}
          />
          {['18%', '48%', '78%'].map((left, nodeIndex) => (
            <div
              className="aevatar-content-skeleton-node"
              data-testid="aevatar-content-skeleton-node"
              key={left}
              style={{
                background: token.colorBgContainer,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: token.borderRadiusLG,
                left,
                padding: 12,
                position: 'absolute',
                top: nodeIndex === 1 ? '52%' : '36%',
                transform: 'translate(-50%, -50%)',
                width: 'min(168px, 24%)',
              }}
            >
              <Skeleton.Input
                active
                size="small"
                style={{ height: 14, maxWidth: '100%', width: '78%' }}
              />
              <Skeleton.Input
                active
                size="small"
                style={{
                  height: 10,
                  marginTop: 8,
                  maxWidth: '100%',
                  width: '54%',
                }}
              />
            </div>
          ))}
        </div>
      </div>
    ) : null;

  return (
    <div
      aria-busy="true"
      className={['aevatar-content-skeleton', className]
        .filter(Boolean)
        .join(' ')}
      data-list-layout={variant === 'list' ? listLayout : undefined}
      data-variant={variant}
      role="status"
      style={{ minWidth: 0, width: '100%', ...style }}
    >
      <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
      <div aria-hidden="true">
        {tablePreset}
        {listPreset}
        {canvasPreset}
      </div>
    </div>
  );
};

export default AevatarContentSkeleton;
