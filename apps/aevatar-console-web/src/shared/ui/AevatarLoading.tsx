import React from 'react';

type LoadingSize = 'small' | 'medium' | 'large' | number;

type LoadingStyle = React.CSSProperties & {
  '--aevatar-loading-color'?: string;
  '--aevatar-loading-dot-size'?: string;
  '--aevatar-loading-gap'?: string;
};

const loadingSizeMap: Record<Exclude<LoadingSize, number>, number> = {
  large: 8,
  medium: 6,
  small: 4,
};
const dotAnimationDelays = [0, 160, 320] as const;

function joinClassNames(
  ...classNames: Array<string | false | null | undefined>
): string | undefined {
  const value = classNames.filter(Boolean).join(' ');
  return value || undefined;
}

function resolveLoadingSize(size: LoadingSize | undefined): string {
  const resolved =
    typeof size === 'number' ? size : loadingSizeMap[size ?? 'medium'];
  return `${resolved}px`;
}

export type AevatarLoadingDotsProps = {
  ariaLabel?: string;
  className?: string;
  color?: string;
  decorative?: boolean;
  gap?: number;
  size?: LoadingSize;
  style?: React.CSSProperties;
};

export function AevatarLoadingDots({
  ariaLabel = 'Loading',
  className,
  color = 'currentColor',
  decorative = false,
  gap,
  size = 'medium',
  style,
}: AevatarLoadingDotsProps): React.ReactElement {
  const mergedStyle: LoadingStyle = {
    '--aevatar-loading-color': color,
    '--aevatar-loading-dot-size': resolveLoadingSize(size),
    ...(gap === undefined
      ? undefined
      : { '--aevatar-loading-gap': `${gap}px` }),
    ...style,
  };

  const dots = dotAnimationDelays.map((delay) => (
    <span
      aria-hidden="true"
      className="aevatar-loading-dot"
      key={delay}
      style={{ animationDelay: `${delay}ms` }}
    />
  ));

  if (decorative) {
    return (
      <span
        aria-hidden="true"
        className={joinClassNames('aevatar-loading-dots', className)}
        style={mergedStyle}
      >
        {dots}
      </span>
    );
  }

  return (
    <span
      className={joinClassNames('aevatar-loading-dots', className)}
      role="status"
      style={mergedStyle}
    >
      <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
      {dots}
    </span>
  );
}

export type AevatarLoadingPulseDotProps = {
  ariaLabel?: string;
  className?: string;
  color?: string;
  decorative?: boolean;
  size?: LoadingSize;
  style?: React.CSSProperties;
};

export function AevatarLoadingPulseDot({
  ariaLabel = 'Loading',
  className,
  color = 'currentColor',
  decorative = true,
  size = 'medium',
  style,
}: AevatarLoadingPulseDotProps): React.ReactElement {
  const mergedStyle: LoadingStyle = {
    '--aevatar-loading-color': color,
    '--aevatar-loading-dot-size': resolveLoadingSize(size),
    ...style,
  };

  if (decorative) {
    return (
      <span
        aria-hidden="true"
        className={joinClassNames('aevatar-loading-pulse-dot', className)}
        style={mergedStyle}
      />
    );
  }

  return (
    <span
      className={joinClassNames('aevatar-loading-pulse-dot', className)}
      role="status"
      style={mergedStyle}
    >
      <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
    </span>
  );
}

export type AevatarStreamingCursorProps = {
  className?: string;
  color?: string;
  style?: React.CSSProperties;
};

export function AevatarStreamingCursor({
  className,
  color = 'currentColor',
  style,
}: AevatarStreamingCursorProps): React.ReactElement {
  const mergedStyle: LoadingStyle = {
    '--aevatar-loading-color': color,
    ...style,
  };

  return (
    <span
      aria-hidden="true"
      className={joinClassNames('aevatar-loading-cursor', className)}
      style={mergedStyle}
    />
  );
}

export type AevatarPageLoadingProps = {
  ariaLabel?: string;
  className?: string;
  fullscreen?: boolean;
  style?: React.CSSProperties;
  tip?: React.ReactNode;
};

export function AevatarPageLoading({
  ariaLabel = 'Loading',
  className,
  fullscreen = false,
  style,
  tip,
}: AevatarPageLoadingProps): React.ReactElement {
  return (
    <div
      aria-busy="true"
      className={joinClassNames(
        'aevatar-page-loading',
        fullscreen && 'aevatar-page-loading-fullscreen',
        className,
      )}
      role="status"
      style={style}
    >
      <AevatarLoadingDots color="#2563eb" decorative size="large" />
      {tip ? (
        <span className="aevatar-page-loading-tip">{tip}</span>
      ) : (
        <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
      )}
    </div>
  );
}

export type AevatarLoadingOverlayProps = {
  ariaLabel: string;
  className?: string;
  style?: React.CSSProperties;
};

export function AevatarLoadingOverlay({
  ariaLabel,
  className,
  style,
}: AevatarLoadingOverlayProps): React.ReactElement {
  return (
    <div
      aria-busy="true"
      aria-label={ariaLabel}
      aria-live="polite"
      className={joinClassNames('aevatar-loading-overlay', className)}
      role="status"
      style={style}
    >
      <AevatarLoadingDots
        color="var(--ant-color-primary, #2563eb)"
        decorative
        size="medium"
      />
      <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
    </div>
  );
}
