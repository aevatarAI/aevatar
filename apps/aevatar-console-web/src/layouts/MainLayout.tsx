import { theme } from "antd";
import React from "react";
import {
  buildAevatarViewportStyle,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";

type MainLayoutProps = {
  children: React.ReactNode;
};

const MainLayout: React.FC<MainLayoutProps> = ({ children }) => {
  const { token } = theme.useToken();

  return (
    <div
      className="aevatar-console-shell"
      style={buildAevatarViewportStyle(token as AevatarThemeSurfaceToken)}
    >
      <div
        style={{
          display: "flex",
          flex: 1,
          flexDirection: "column",
          minHeight: 0,
        }}
      >
        {children}
      </div>
    </div>
  );
};

export default MainLayout;
