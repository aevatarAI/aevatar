import React, { type ReactNode } from 'react';

type TableScrollRegionProps = {
  readonly ariaLabel: string;
  readonly children: ReactNode;
  readonly className?: string;
};

// Keep overflow keyboard-scrollable in browsers that do not auto-focus scroll regions.
const keyboardScrollProps = { tabIndex: 0 } as const;

const TableScrollRegion = ({
  ariaLabel,
  children,
  className,
}: TableScrollRegionProps) => (
  <section
    aria-label={ariaLabel}
    className={[
      'wa-vnext__table-wrap',
      'wa-vnext__table-wrap--cards',
      className,
    ]
      .filter(Boolean)
      .join(' ')}
    {...keyboardScrollProps}
  >
    {children}
  </section>
);

export default TableScrollRegion;
