export type FloatingOffset = {
  x: number;
  y: number;
};

export const DEFAULT_FLOATING_OFFSET: FloatingOffset = {
  x: 0,
  y: 0,
};

export type FloatingBounds = {
  baseLeft: number;
  baseTop: number;
  containerWidth: number;
  containerHeight: number;
  floatingWidth: number;
  floatingHeight: number;
};

function clampToRange(value: number, min: number, max: number) {
  if (max < min) {
    return min;
  }

  return Math.min(Math.max(value, min), max);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

export function normalizeFloatingOffset(value: unknown): FloatingOffset {
  if (
    value &&
    typeof value === 'object' &&
    isFiniteNumber((value as FloatingOffset).x) &&
    isFiniteNumber((value as FloatingOffset).y)
  ) {
    return {
      x: (value as FloatingOffset).x,
      y: (value as FloatingOffset).y,
    };
  }

  return DEFAULT_FLOATING_OFFSET;
}

export function readFloatingOffsetFromStorage(storageValue: string | null): FloatingOffset {
  if (!storageValue) {
    return DEFAULT_FLOATING_OFFSET;
  }

  try {
    return normalizeFloatingOffset(JSON.parse(storageValue));
  } catch {
    return DEFAULT_FLOATING_OFFSET;
  }
}

export function clampFloatingOffset(
  offset: FloatingOffset,
  bounds: FloatingBounds,
): FloatingOffset {
  const minX = -bounds.baseLeft;
  const maxX = bounds.containerWidth - bounds.floatingWidth - bounds.baseLeft;
  const minY = -bounds.baseTop;
  const maxY = bounds.containerHeight - bounds.floatingHeight - bounds.baseTop;

  return {
    x: clampToRange(offset.x, minX, maxX),
    y: clampToRange(offset.y, minY, maxY),
  };
}
