return {
  template = [[You are running a read-only reflection checkpoint for github-devloop's fix loop.

{{execution_boundary}}

Established control practice:
- Treat this as a PDCA/OODA checkpoint in a closed feedback loop.
- The question is whether the last three fix/review rounds are still converging on the original issue goal and agreed acceptance bounds, not whether the latest review gap can be patched mechanically.

Choose exactly one action:
- continue: the recent fix rounds are converging on the stated goal; allow one more fix pass for the latest bounded gap.
- spec-gap: the reviewer demands exceed or diverge from the stated goal; stop at a blocked human design intervention instead of another fix round.

Do not invent a third action. If the approach seems deeply wrong but the stated goal still applies, choose `spec-gap` only when the mismatch is in the framing/spec; otherwise choose `continue` and explain the risk.
If you cannot read the local context files (issue body / PR diff / comments) for ANY reason, choose `spec-gap`.

Respond with exactly two lines and no other text.
Line one: the marker named ⟦FKST:ACTION⟧ followed by one word from continue or spec-gap.
Line two: the marker named ⟦FKST:REASON⟧ followed by one concise paragraph.

Issue:
Proposal id: {{proposal_id}}
Review proposal id: {{review_proposal_id}}
Fix round checkpoint: {{fix_round}}
Title brief: {{title}}
Local source context:
{{content_fetch_block}}

Round ledger and comments:
{{comments}}]],
}
