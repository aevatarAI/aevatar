const React = require('react');

const iconCache = new Map();

function createMockIcon(displayName) {
  if (iconCache.has(displayName)) {
    return iconCache.get(displayName);
  }

  const Icon = React.forwardRef(function MockAntdIcon(props, ref) {
    const { className, style } = props || {};

    return React.createElement('span', {
      'aria-hidden': 'true',
      className,
      'data-icon-mock': displayName,
      ref,
      style,
    });
  });

  Icon.displayName = displayName;
  iconCache.set(displayName, Icon);
  return Icon;
}

const iconModule = new Proxy(
  {},
  {
    get(_target, property) {
      if (property === '__esModule') {
        return true;
      }

      if (property === 'default') {
        return iconModule;
      }

      if (property === 'createFromIconfontCN') {
        return () => createMockIcon('IconFont');
      }

      if (property === 'getTwoToneColor') {
        return () => '#1677ff';
      }

      if (property === 'setTwoToneColor') {
        return () => undefined;
      }

      return createMockIcon(String(property));
    },
  },
);

module.exports = iconModule;
module.exports.default = iconModule;
module.exports.__esModule = true;
