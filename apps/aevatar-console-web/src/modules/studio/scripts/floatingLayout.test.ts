import {
  clampFloatingOffset,
  DEFAULT_FLOATING_OFFSET,
  normalizeFloatingOffset,
  readFloatingOffsetFromStorage,
} from './floatingLayout';

describe('floatingLayout', () => {
  it('normalizes invalid offsets back to the default position', () => {
    expect(normalizeFloatingOffset({ x: 12, y: -18 })).toEqual({
      x: 12,
      y: -18,
    });
    expect(normalizeFloatingOffset({ x: '12', y: -18 })).toEqual(
      DEFAULT_FLOATING_OFFSET,
    );
  });

  it('reads persisted offsets defensively', () => {
    expect(readFloatingOffsetFromStorage('{"x":64,"y":-24}')).toEqual({
      x: 64,
      y: -24,
    });
    expect(readFloatingOffsetFromStorage('{"x":"bad"}')).toEqual(
      DEFAULT_FLOATING_OFFSET,
    );
    expect(readFloatingOffsetFromStorage('{bad json')).toEqual(
      DEFAULT_FLOATING_OFFSET,
    );
  });

  it('keeps the floating surface inside the viewport bounds', () => {
    expect(
      clampFloatingOffset(
        { x: 180, y: -240 },
        {
          baseLeft: 500,
          baseTop: 420,
          containerWidth: 1200,
          containerHeight: 800,
          floatingWidth: 420,
          floatingHeight: 360,
        },
      ),
    ).toEqual({ x: 180, y: -240 });
  });

  it('clamps the floating surface when dragging beyond the viewport edges', () => {
    expect(
      clampFloatingOffset(
        { x: 520, y: 240 },
        {
          baseLeft: 700,
          baseTop: 500,
          containerWidth: 1200,
          containerHeight: 800,
          floatingWidth: 420,
          floatingHeight: 360,
        },
      ),
    ).toEqual({ x: 80, y: -60 });
  });
});
