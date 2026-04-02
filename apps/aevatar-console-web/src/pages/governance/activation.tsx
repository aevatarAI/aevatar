import React, { useEffect } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildGovernanceWorkbenchHref,
  readGovernanceDraft,
} from "./components/governanceQuery";

const GovernanceActivationPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildGovernanceWorkbenchHref(readGovernanceDraft(), "activation"),
    );
  }, []);

  return null;
};

export default GovernanceActivationPage;
