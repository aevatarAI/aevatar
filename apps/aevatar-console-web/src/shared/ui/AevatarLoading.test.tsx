import { render, screen } from '@testing-library/react';
import React from 'react';
import {
  AevatarLoadingDots,
  AevatarLoadingOverlay,
  AevatarLoadingPulseDot,
  AevatarPageLoading,
  AevatarStreamingCursor,
} from './AevatarLoading';

describe('AevatarLoading', () => {
  it('renders animated dots with staggered delays', () => {
    const { container } = render(
      <AevatarLoadingDots
        ariaLabel="Assistant is responding"
        color="#d1d5db"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent(
      'Assistant is responding',
    );
    expect(container.querySelectorAll('.aevatar-loading-dot')).toHaveLength(3);
    expect(container.querySelectorAll('.aevatar-loading-dot')[1]).toHaveStyle({
      animationDelay: '160ms',
    });
  });

  it('can render pulse dots as decorative status accents', () => {
    const { container } = render(
      <AevatarLoadingPulseDot color="#f59e0b" size={8} />,
    );

    expect(
      container.querySelector('.aevatar-loading-pulse-dot'),
    ).toHaveAttribute('aria-hidden', 'true');
    expect(container.querySelector('.aevatar-loading-pulse-dot')).toHaveStyle({
      '--aevatar-loading-dot-size': '8px',
    });
  });

  it('keeps streaming cursors hidden from assistive technologies', () => {
    const { container } = render(<AevatarStreamingCursor color="#9ca3af" />);

    expect(container.querySelector('.aevatar-loading-cursor')).toHaveAttribute(
      'aria-hidden',
      'true',
    );
  });

  it('uses the shared dots for page loading states', () => {
    render(<AevatarPageLoading fullscreen tip="Loading console" />);

    expect(screen.getByRole('status')).toHaveClass(
      'aevatar-page-loading-fullscreen',
    );
    expect(screen.getByText('Loading console')).toBeInTheDocument();
  });

  it('announces a default page loading label without a visible tip', () => {
    render(<AevatarPageLoading />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading');
    expect(screen.getByText('Loading')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
  });

  it('uses the shared loading language for committed-content overlays', () => {
    const { container } = render(
      <AevatarLoadingOverlay ariaLabel="Refreshing run details" />,
    );

    const status = screen.getByRole('status', {
      name: 'Refreshing run details',
    });
    expect(status).toHaveAttribute('aria-busy', 'true');
    expect(status).toHaveClass('aevatar-loading-overlay');
    expect(container.querySelectorAll('.aevatar-loading-dot')).toHaveLength(3);
    expect(screen.getByText('Refreshing run details')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
  });
});
