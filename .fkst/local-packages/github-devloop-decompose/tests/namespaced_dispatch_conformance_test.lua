local devloop_base = require("devloop.base")
local core = require("core")
local conformance = require("testkit.namespaced_dispatch_conformance")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = fkst.test
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local function load_department(path, module_name)
  local old_pipeline = pipeline
  local module = require(module_name)
  pipeline = old_pipeline
  return { path = path, module = module }
end

local departments = conformance.loaded_departments({
  load_department("departments/decompose/main.lua", "departments.decompose.main"),
})

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

local function payload_for_queue(_path, queue)
  if queue == "devloop_decompose" then
    return production_decompose_payload()
  end
  error("github-devloop-decompose: no production-shaped queue fixture for " .. tostring(queue))
end

local function mock_decompose_reads(payload)
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
end

local function opts_for_case(_path, _queue, event)
  mock_decompose_reads(event.payload)
  return {
    run_opts = {
      env = {
        FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      },
    },
    before_replay = function()
      mock_decompose_reads(event.payload)
    end,
  }
end

return {
  test_all_departments_accept_production_namespaced_consumed_queues = function()
    conformance.assert_all_consumed_queues_route({
      t = t,
      package_name = "github-devloop-decompose",
      package_root = "packages/github-devloop-decompose",
      departments = departments,
      payload_for_queue = payload_for_queue,
      opts_for_case = opts_for_case,
    })
  end,
}
