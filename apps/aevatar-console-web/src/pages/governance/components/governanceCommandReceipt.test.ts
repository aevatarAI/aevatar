import type { ServicePolicyCatalogSnapshot } from "@/shared/models/governance";
import { observeGovernanceReceipt, type GovernanceCommandReceipt } from "./governanceCommandReceipt";

const receipt: GovernanceCommandReceipt = {
  acceptedAt: "2026-06-02T09:30:00Z",
  catalogKind: "policies",
  commandLabel: "Policy policy-a was accepted for update.",
  targetId: "policy-a",
};

function buildPolicyCatalog(updatedAt: string): ServicePolicyCatalogSnapshot {
  return {
    policies: [],
    serviceKey: "tenant/app/default/service",
    updatedAt,
  };
}

describe("governanceCommandReceipt", () => {
  it("does not treat a stale catalog as observed evidence", () => {
    expect(
      observeGovernanceReceipt(
        receipt,
        buildPolicyCatalog("2026-06-02T09:29:59Z"),
      ),
    ).toMatchObject({
      catalogLabel: "Policy catalog",
      observed: false,
    });
  });

  it("marks the receipt observed after the matching catalog refreshes", () => {
    expect(
      observeGovernanceReceipt(
        receipt,
        buildPolicyCatalog("2026-06-02T09:31:00Z"),
      ),
    ).toMatchObject({
      catalogLabel: "Policy catalog",
      observed: true,
    });
  });

  it("keeps missing catalogs in accepted-only state", () => {
    expect(observeGovernanceReceipt(receipt, null)).toMatchObject({
      observed: false,
      summary: expect.stringContaining("only command acceptance is known"),
    });
  });
});
