import { addLocale } from '@umijs/max';
import { createLocaleCatalogLoader } from './createLocaleCatalogLoader';
import { resolveAntdLocale } from './localeProvider';

export const loadConsoleLocale = createLocaleCatalogLoader(
  {
    'en-US': () =>
      import(
        /* webpackChunkName: "console-locale-en-US" */ '@/locales/messages/en-US'
      ),
    'zh-CN': () =>
      import(
        /* webpackChunkName: "console-locale-zh-CN" */ '@/locales/messages/zh-CN'
      ),
  },
  (locale, messages) => {
    addLocale(locale, messages, {
      antd: resolveAntdLocale(locale),
      momentLocale: locale === 'en-US' ? 'en' : 'zh-cn',
    });
  },
);
