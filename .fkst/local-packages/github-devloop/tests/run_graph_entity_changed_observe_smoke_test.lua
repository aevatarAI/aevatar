local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local graph = require("testkit.graph")
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local t = h.t
local core = h.core

local repo = "owner/repo"
local issue_number = 42
local proposal_id = base_ids.proposal_id(repo, issue_number)
local blocked_version = "blocked/github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function observe_spec()
  return require("departments.observe_issue.main").spec
end

local function source_ref()
  return entity_lib.issue_source_ref(repo, issue_number)
end

local function initial_event()
  return {
    queue = "github-proxy.github_entity_changed",
    payload = {
      schema = "github-proxy.v1",
      type = "issue",
      repo = repo,
      number = issue_number,
      title = "Entry routing issue",
      updated_at = "2026-06-03T01:02:03Z",
      dedup_key = "owner/repo#issue/42@2026-06-03T01:02:03Z",
      source_ref = source_ref(),
    },
    source_ref = {
      kind = "external",
      reference = "owner/repo#issue/42",
    },
  }
end

local function mock_runtime_and_context()
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
    t.mock_command(devloop_base.read_env_command("FKST_GITHUB_CLAIM_MODE"), {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_blocked_issue_with_stale_label()
  entity_read_mocks.mock_issue_read_with_defaults(
    t,
    { "fkst-dev:enabled", "fkst-dev:thinking" },
    {
      core.state_marker(proposal_id, "blocked", blocked_version),
    },
    {
      repo = repo,
      number = issue_number,
      title = "Entry routing issue",
      updated_at = "2026-06-03T01:02:03Z",
      state = "OPEN",
      assignees = { "fkst-test-bot" },
      author_login = "fkst-test-bot",
      times = 4,
    }
  )
  t.mock_command(core.gh_issue_list_decompose_children_cmd(repo, proposal_id), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

return {
  test_run_graph_entity_changed_delivers_to_observe_issue_and_raises_forward_action = function()
    mock_runtime_and_context()
    mock_blocked_issue_with_stale_label()

    local trace = graph.require_quiescent(graph.run(initial_event(), { max_steps = 4 }))
    graph.assert_covers(trace, {
      "github-proxy.github_entity_changed -> github-devloop.observe_issue",
      "github-proxy.github_issue_label_request -> github-proxy.github_issue_label",
    })

    local route = graph.require_router_regression(trace, {
      spec = observe_spec(),
      entry_queue = "github-proxy.github_entity_changed",
      consumer = "github-devloop.observe_issue",
      raised_queue = "github-proxy.github_issue_label_request",
      downstream_consumer = "github-proxy.github_issue_label",
      raised_predicate = function(raised)
        local payload = raised.payload or {}
        return payload.schema == "github-proxy.label.v1"
          and payload.repo == repo
          and tonumber(payload.issue_number) == issue_number
          and payload.add_labels ~= nil
          and payload.add_labels[1] == "fkst-dev:blocked"
          and graph.payload_contains(raised, proposal_id)
      end,
    })

    local label_request = route.raised
    t.eq(label_request.payload.source_ref.ref, "owner/repo#issue/42")
  end,
}
