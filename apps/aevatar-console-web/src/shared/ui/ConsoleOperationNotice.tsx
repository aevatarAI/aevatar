import { Alert } from 'antd';
import * as React from 'react';
import { useConsoleToast } from './ConsoleToast';

export type ConsoleOperationNoticeValue = {
  readonly message: string;
  readonly type: 'error' | 'info' | 'success' | 'warning';
};

type ConsoleOperationNoticeProps = {
  readonly errorMessage: string;
  readonly notice: ConsoleOperationNoticeValue | null | undefined;
  readonly onClose?: () => void;
};

const ConsoleOperationNotice: React.FC<ConsoleOperationNoticeProps> = ({
  errorMessage,
  notice,
  onClose,
}) => {
  const toast = useConsoleToast();
  const shownErrorRef = React.useRef<ConsoleOperationNoticeValue | null>(null);

  React.useEffect(() => {
    if (notice?.type !== 'error') {
      shownErrorRef.current = null;
      return;
    }
    if (shownErrorRef.current === notice) return;
    shownErrorRef.current = notice;
    toast.error(errorMessage);
    onClose?.();
  }, [errorMessage, notice, onClose, toast]);

  if (!notice || notice.type === 'error') return null;

  return (
    <Alert
      closable={Boolean(onClose)}
      message={notice.message}
      onClose={onClose}
      showIcon
      type={notice.type}
    />
  );
};

export default ConsoleOperationNotice;
