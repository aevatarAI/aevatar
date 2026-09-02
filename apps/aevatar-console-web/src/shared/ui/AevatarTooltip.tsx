import { Tooltip, type TooltipProps } from 'antd';
import React from 'react';

const DEFAULT_TOOLTIP_CONTAINER_STYLE: React.CSSProperties = {
  lineHeight: 1.5,
  maxWidth: 320,
  overflowWrap: 'anywhere',
};

const DEFAULT_TOOLTIP_TRIGGER: TooltipProps['trigger'] = ['hover', 'focus'];

const AevatarTooltip: React.FC<TooltipProps> = ({
  mouseEnterDelay = 0.2,
  placement = 'top',
  styles,
  trigger = DEFAULT_TOOLTIP_TRIGGER,
  ...props
}) => (
  <Tooltip
    {...props}
    mouseEnterDelay={mouseEnterDelay}
    placement={placement}
    styles={(info) => {
      const resolvedStyles =
        typeof styles === 'function' ? styles(info) : styles;
      return {
        ...resolvedStyles,
        container: {
          ...DEFAULT_TOOLTIP_CONTAINER_STYLE,
          ...resolvedStyles?.container,
        },
      };
    }}
    trigger={trigger}
  />
);

export default AevatarTooltip;
