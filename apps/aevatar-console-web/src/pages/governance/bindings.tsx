import React, { useEffect } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildGovernanceWorkbenchHref,
  readGovernanceDraft,
} from "./components/governanceQuery";

const GovernanceBindingsPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildGovernanceWorkbenchHref(readGovernanceDraft(), "bindings"),
    );
  }, []);

  return null;
};

export default GovernanceBindingsPage;
