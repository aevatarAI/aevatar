import { observeDraftMaterialization } from "./useDraftMaterialization";

describe("observeDraftMaterialization", () => {
  it("treats bounded 404 as projection delay and keeps observing the exact receipt id", async () => {
    const pending = Object.assign(new Error("pending"), { status: 404 });
    const read = jest
      .fn()
      .mockRejectedValueOnce(pending)
      .mockRejectedValueOnce(pending)
      .mockResolvedValueOnce({ workflowId: "wf-api-returned" });

    await expect(
      observeDraftMaterialization({
        workflowId: "wf-api-returned",
        read,
        isNotFound: (error) => error === pending,
        wait: async () => undefined,
        delaysMs: [0, 0, 0],
      }),
    ).resolves.toEqual({ kind: "readable", workflow: { workflowId: "wf-api-returned" } });
    expect(read.mock.calls).toEqual([
      ["wf-api-returned"],
      ["wf-api-returned"],
      ["wf-api-returned"],
    ]);
  });

  it("returns delayed without inventing success and retry reads the same id", async () => {
    const pending = Object.assign(new Error("pending"), { status: 404 });
    const read = jest.fn().mockRejectedValue(pending);
    const input = {
      workflowId: "wf-api-returned",
      read,
      isNotFound: (error: unknown) => error === pending,
      wait: async () => undefined,
      delaysMs: [0, 0],
    };

    await expect(observeDraftMaterialization(input)).resolves.toEqual({ kind: "delayed" });
    await expect(observeDraftMaterialization(input)).resolves.toEqual({ kind: "delayed" });
    expect(read).toHaveBeenCalledTimes(4);
    expect(new Set(read.mock.calls.map(([workflowId]) => workflowId))).toEqual(
      new Set(["wf-api-returned"]),
    );
  });

  it("surfaces non-404 failures immediately", async () => {
    const forbidden = Object.assign(new Error("forbidden"), { status: 403 });
    const read = jest.fn().mockRejectedValue(forbidden);

    await expect(
      observeDraftMaterialization({
        workflowId: "wf-api-returned",
        read,
        isNotFound: () => false,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).rejects.toBe(forbidden);
    expect(read).toHaveBeenCalledTimes(1);
  });
});
