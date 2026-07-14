local devloop_base = require("devloop.base")
local h = require("tests.devloop_helpers")
local graph = require("testkit.graph")
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = h.t
local core = h.core
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#pr/7",
  }
end

local function decompose_payload()
  return payloads_builders.build_devloop_decompose_payload(core, {
    proposal_id = "github-devloop/issue/owner/repo/42",
    pr_number = 7,
    issue_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3",
    review_proposal_id = devloop_base.pr_review_proposal_id(
      "owner/repo",
      7,
      "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3",
      "def456"
    ),
    review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id(
      "owner/repo",
      7,
      "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3",
      "def456"
    ) .. "/review",
    reviewed_head_sha = "def456",
    head_sha = "def456",
    round = 3,
    source_ref = source_ref(),
  })
end

local function mock_env()
  for _ = 1, 8 do
    t.mock_command(devloop_base.read_env_command("FKST_GITHUB_BOT_LOGIN"), {
      stdout = "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command(devloop_base.read_env_command("FKST_GITHUB_WRITE"), {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop-decompose-run-graph/runtime",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_claim_and_reads(payload)
  h.mock_default_issue_claim("owner/repo", 42)
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = "owner/repo",
    number = 42,
    title = "Original large issue",
    body = "Child body.\n\n" .. decompose_lib.decompose_lineage_marker(core, payload.proposal_id, 1),
    labels = { "fkst-dev:blocked" },
    comments = {
      core.state_marker(payload.proposal_id, "blocked", payload.version),
      conv_reconcile.fix_reconcile_marker(core, payload.proposal_id, payload.version, "drop"),
    },
    state = "OPEN",
    updated_at = "2026-06-03T01:02:03Z",
  }, "title,body,labels,comments")
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = "owner/repo",
    number = 7,
    comments = {
      m_builders.pr_origin_marker(core, payload.proposal_id, 7, "devloop-owner-repo-42-01HY", payload.version, "dev"),
      core.state_marker(payload.proposal_id, "blocked", payload.version),
      conv_reconcile.fix_reconcile_marker(core, payload.proposal_id, payload.version, "drop"),
    },
    head = "devloop-owner-repo-42-01HY",
    head_sha = "def456",
    base_branch = "dev",
    state = "OPEN",
    updated_at = "2026-06-03T02:03:04Z",
  }, entity_read_mocks.pr_origin_selector, 2)
  t.mock_command(core.gh_issue_list_decompose_children_cmd("owner/repo", payload.proposal_id), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_decompose_codex()
  t.mock_command("mktemp -d", {
    stdout = "/tmp/fkst-packages-test/github-devloop-decompose-run-graph/context/.bundle-tmp.decompose\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("install -d -m 0755", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("test -d", {
    stdout = "",
    stderr = "",
    exit_code = 1,
  })
  entity_read_mocks.mock_issue_view_raw_selector(t, {}, "title,body,updatedAt,labels,comments,state", {
    stdout = '{"title":"Original large issue","body":"Original body","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[{"name":"fkst-dev:blocked"}],"comments":[]}\n',
  })
  entity_read_mocks.mock_pr_view_raw_selector(t, {}, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels", {
    stdout = '{"title":"PR title","body":"PR body","headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"OPEN","updatedAt":"2026-06-03T02:03:04Z","comments":[],"labels":[]}\n',
  })
  t.mock_command("gh pr diff", {
    stdout = "diff --git a/file.lua b/file.lua\n+return true\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh pr diff '7' --repo 'owner/repo' --name-only", {
    stdout = "file.lua\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("mkdir -p", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("python3 -c", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("test -r", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("wc -c < ", {
    stdout = "1\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("codex exec", {
    stdout = [[{"issues":[{"title":"Extract a minimal retry helper","body":"Smaller scope: implement only the retry helper used by the blocked PR.\nNon-goals: do not change the whole workflow.\nAcceptance: helper tests pass."},{"title":"Wire retry helper into one call site","body":"Smaller scope: apply the helper to one review-gate path.\nNon-goals: do not rewrite unrelated states.\nAcceptance: focused integration test passes."}]}]],
    stderr = "",
    exit_code = 0,
  })
end

local function initial_event()
  return {
    queue = "github-devloop-decompose.devloop_decompose",
    payload = decompose_payload(),
    source_ref = {
      kind = "external",
      reference = "owner/repo#pr/7",
    },
  }
end

return {
  test_run_graph_decompose_routes_devloop_decompose_to_decompose = function()
    local payload = decompose_payload()
    mock_env()
    mock_claim_and_reads(payload)
    mock_decompose_codex()

    local trace = graph.require_quiescent(graph.run(initial_event(), { max_steps = 6 }))
    graph.assert_covers(trace, {
      "github-devloop-decompose.devloop_decompose -> github-devloop-decompose.decompose",
    })

    local step = graph.require_delivery(trace, {
      queue = "github-devloop-decompose.devloop_decompose",
      consumer = "github-devloop-decompose.decompose",
    })
    t.eq(step.exit_code, 0)
  end,
}
