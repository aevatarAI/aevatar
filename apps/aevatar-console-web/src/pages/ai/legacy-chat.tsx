import React from 'react';
import { t } from '@/shared/i18n/messages';
import { buildLegacyChatRedirectHref } from '@/shared/navigation/aiRoutes';
import { history } from '@/shared/navigation/history';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';

const LegacyChatRedirectPage: React.FC = () => {
  React.useEffect(() => {
    history.replace(buildLegacyChatRedirectHref());
  }, []);

  return (
    <AevatarPageLoading
      ariaLabel={t('pages.ai.legacyChat.loading.ariaLabel', 'Opening AI Chat')}
      tip={t('pages.ai.legacyChat.loading.description', 'Opening AI Chat')}
    />
  );
};

export default LegacyChatRedirectPage;
