import React from 'react';

type BrandLogoProps = {
  size?: number;
};

export default function BrandLogo({ size = 28 }: BrandLogoProps) {
  return (
    <img
      alt="Aevatar"
      src="/favicon-32x32.png"
      style={{
        borderRadius: Math.max(6, Math.round(size * 0.22)),
        display: 'block',
        height: size,
        width: size,
      }}
    />
  );
}
