local devloop_base = require("devloop.base")
local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = fkst.test
local core = require("core")
local h = require("tests.devloop_helpers")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local function production_decompose_payload()
  return payloads_builders.build_devloop_decompose_payload(core, conv_reconcile.build_devloop_fix_reconcile_payload(core, {
    proposal_id = "github-devloop/issue/owner/repo/42",
    review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3", "def456"),
    review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3", "def456") .. "/review",
    reviewed_head_sha = "def456",
    pr_number = 7,
    source_ref = { kind = "external", ref = "owner/repo#pr/7" },
  }, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3"))
end

return {
  test_decompose_consumes_production_namespaced_queue_without_queue_fallthrough = function()
    local payload = production_decompose_payload()
    h.mock_default_issue_claim()
    entity_read_mocks.mock_issue_view_selector(t, {
      labels = { "fkst-dev:blocked" },
      comments = {
        core.state_marker(payload.proposal_id, "blocked", payload.version),
      },
      title = "Original large issue",
      body = "Child body.\n\n" .. decompose_lib.decompose_lineage_marker(core, payload.proposal_id, 1),
    }, "title,body,labels,comments")
    entity_read_mocks.mock_pr_view_selector(t, {
      comments = {
        m_builders.pr_origin_marker(core, payload.proposal_id, "42", "devloop-owner-repo-42-01HY", payload.version, "dev"),
        core.state_marker(payload.proposal_id, "blocked", payload.version),
        conv_reconcile.fix_reconcile_marker(core, payload.proposal_id, payload.version, "drop"),
      },
      head = "devloop-owner-repo-42-01HY",
      head_sha = "def456",
      base_branch = "dev",
      state = "OPEN",
    }, entity_read_mocks.pr_origin_selector, 2)
    local result = t.run_department("departments/decompose/main.lua", {
      queue = "github-devloop-decompose.devloop_decompose",
      payload = payload,
    }, {
      env = {
        FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      },
    })

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github-proxy.github_pr_comment_request")
    t.is_true(result.raises[1].payload.body:find("fkst:github-devloop:decompose-exhausted:v1", 1, true) ~= nil)
    t.is_true(tostring(result.stderr or ""):find("unsupported event payload", 1, true) == nil)
    t.is_true(tostring(result.stderr or ""):find("skip-foreign(payload)", 1, true) == nil)
  end,

  test_decompose_skips_non_table_payloads = function()
    for _, payload in ipairs({ false, "foreign-payload", 42 }) do
      local result = t.run_department("departments/decompose/main.lua", {
        queue = "devloop_decompose",
        payload = payload,
      })

      t.eq(result.exit_code, 0)
      t.eq(#result.raises, 0)
    end
  end,
}
