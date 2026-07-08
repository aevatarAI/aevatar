import { Button, Card, Result } from 'antd';
import React from 'react';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { history } from '@/shared/navigation/history';
import { t } from "@/shared/i18n/messages";

const NoFoundPage: React.FC = () => (
  <Card variant="borderless">
    <Result
      status="404"
      title="404"
      subTitle="The requested page does not exist."
      extra={
        <Button
          type="primary"
          onClick={() => {
            history.push(CONSOLE_HOME_ROUTE);
          }}
        >
          {t("pages.404.return.to.projects", "Return to projects")}</Button>
      }
    />
  </Card>
);

export default NoFoundPage;
