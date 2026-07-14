local h = require("tests.devloop_helpers")
local conv_rounds = require("devloop.convergence.rounds")
local t = h.t
local core = h.core
local opts = h.opts
local unresolved = h.unresolved
local run_loop = h.run_loop
local mock_issue_loop = h.mock_issue_loop
local find_raise = h.find_raise

return {
  test_loop_reraised_proposal_dedup_follows_consensus_lineage_not_current_updated_at = function()
    local base_version = "consensus:github-devloop/issue/owner/repo/42/intake/1234567890"
    mock_issue_loop({ "fkst-dev:thinking" }, {
      core.state_marker("github-devloop/issue/owner/repo/42", "thinking", base_version),
    }, {
      updated_at = "2026-06-14T01:02:03Z",
    })

    local event = unresolved({
      dedup_key = base_version,
      narrowed_question = "Can this proceed after narrowing?",
      angle_digests = {
        { angle = "minimal", verdict = "abstain", digest = "needs-specificity" },
      },
    })
    local result = run_loop(event, opts("loop-dedup-lineage"))
    t.eq(result.exit_code, 0)
    local proposal = find_raise(result.raises, "consensus.proposal").payload
    t.eq(proposal.dedup_key, conv_rounds.converge_proposal_base_dedup(core, base_version) .. "/loop/1")
    t.eq(proposal.round, 1)
    t.eq(proposal.convergence_question, event.narrowed_question)
    t.eq(proposal.source_ref.ref, "owner/repo#issue/42")
    t.is_true(proposal.content_fetch:find("runtime-cache:", 1, true) == 1)

    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request").payload
    t.is_true(comment.body:find('version="' .. base_version .. '"', 1, true) ~= nil)
    t.is_true(comment.body:find('round="0"', 1, true) ~= nil)
  end,
}
