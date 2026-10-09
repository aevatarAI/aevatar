import React from 'react';
import { t } from '@/shared/i18n/messages';

const TechnicalDetails: React.FC<{
  readonly children: React.ReactNode;
  readonly summary?: string;
}> = ({ children, summary }) => (
  <details className="wa-vnext__technical-details">
    <summary>
      {summary ||
        t('workflowActivityVNext.common.technicalDetails', 'Technical details')}
    </summary>
    <div className="wa-vnext__technical-details-body" translate="no">
      {children}
    </div>
  </details>
);

export default TechnicalDetails;
