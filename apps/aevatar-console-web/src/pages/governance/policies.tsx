import React, { useEffect } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildGovernanceWorkbenchHref,
  readGovernanceDraft,
} from "./components/governanceQuery";

const GovernancePoliciesPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildGovernanceWorkbenchHref(readGovernanceDraft(), "policies"),
    );
  }, []);

  return null;
};

export default GovernancePoliciesPage;
