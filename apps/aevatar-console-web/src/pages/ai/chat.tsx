import React from 'react';
import ChatPage from '@/pages/chat';
import { t } from '@/shared/i18n/messages';
import { AI_CHAT_ROUTE } from '@/shared/navigation/aiRoutes';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from './components/AIWorkspaceShell';

export const AIChatContent: React.FC = () => {
  const { context } = useAIWorkspaceContext();
  const chatDeclared =
    context.pages.chat === AI_CHAT_ROUTE &&
    context.apis.chat === '/api/chat' &&
    context.features.chat?.availability === 'available' &&
    context.features.chat.page === AI_CHAT_ROUTE &&
    context.features.chat.api === context.apis.chat;

  if (!chatDeclared) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.chat.notAvailable.description',
            'The backend has not enabled Chat for this workspace.',
          )}
          kind="empty"
          title={t('pages.ai.chat.notAvailable.title', 'Chat not available')}
        />
      </div>
    );
  }

  return <ChatPage />;
};

const AIChatPage: React.FC = () => (
  <AIWorkspaceShell>
    <AIChatContent />
  </AIWorkspaceShell>
);

export default AIChatPage;
