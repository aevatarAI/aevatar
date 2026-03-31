import React, { useEffect } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildGovernanceWorkbenchHref,
  readGovernanceDraft,
} from "./components/governanceQuery";

const GovernanceEndpointsPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildGovernanceWorkbenchHref(readGovernanceDraft(), "endpoints"),
    );
  }, []);

  return null;
};

export default GovernanceEndpointsPage;
