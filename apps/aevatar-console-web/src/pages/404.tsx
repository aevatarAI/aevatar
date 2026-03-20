import { Button, Card, Result } from 'antd';
import React from 'react';

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
            window.location.href = '/overview';
          }}
        >
          Return to overview
        </Button>
      }
    />
  </Card>
);

export default NoFoundPage;
