import { theme } from "antd";
import React from "react";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { syncStudioHostBodyClass } from "@/shared/studio/studioLayout";
import {
  buildAevatarViewportStyle,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";

type MainLayoutProps = {
  children: React.ReactNode;
};

const MainLayout: React.FC<MainLayoutProps> = ({ children }) => {
  const { token } = theme.useToken();
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => "",
  );

  React.useEffect(() => {
    const pathname = locationSnapshot.split("?")[0]?.split("#")[0] ?? "";
    return syncStudioHostBodyClass(pathname === "/studio");
  }, [locationSnapshot]);

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
