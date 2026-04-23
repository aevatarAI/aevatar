import React from "react";
import AccountSettingsContent from "./accountContent";
import { SettingsPageShell } from "./shared";

const AccountSettingsPage: React.FC = () => {
  return (
    <SettingsPageShell title="Account Settings">
      <AccountSettingsContent />
    </SettingsPageShell>
  );
};

export default AccountSettingsPage;
