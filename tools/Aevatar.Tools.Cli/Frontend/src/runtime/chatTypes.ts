import type { RuntimeEvent } from './sseUtils';

export type ChatMessage = {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: number;
  status: 'complete' | 'streaming' | 'error';
  error?: string;
  events?: RuntimeEvent[];
};

export type ServiceOption = {
  id: string;
  label: string;
  kind: 'nyxid-chat' | 'service';
};
