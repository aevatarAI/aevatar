import { Button, Card, Result } from 'antd';
import React from 'react';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';

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
            window.location.href = CONSOLE_HOME_ROUTE;
          }}
        >
          Return to projects
        </Button>
      }
    />
  </Card>
);

export default NoFoundPage;
