import { Button, Card, Result } from 'antd';
import React from 'react';
import { replaceAppLocation } from '@/shared/navigation/appPath';

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
            replaceAppLocation('/overview');
          }}
        >
          Return to overview
        </Button>
      }
    />
  </Card>
);

export default NoFoundPage;
