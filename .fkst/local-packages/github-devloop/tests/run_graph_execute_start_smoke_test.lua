local devloop_base = require("devloop.base")
local h = require("tests.devloop_helpers")
local execution_start = require("devloop.execution_start")
local graph = require("testkit.graph")
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local t = h.t
local core = h.core

local repo = "owner/repo"
local issue_number = 42
local proposal_id = "github-devloop/issue/owner/repo/42"
local request_dedup_key = "intake/github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local verdict_label = "⟦FKST:VERDICT⟧"
local reply_label = "⟦FKST:REPLY⟧"

local function source_ref()
  return {
    kind = "external",
    ref = repo .. "#issue/" .. tostring(issue_number),
  }
end

local function execution_request()
  return execution_start.build_execution_request_payload({
    proposal_id = proposal_id,
    dedup_key = request_dedup_key,
    source_ref = source_ref(),
    origin = {
      package = "github-devloop-intake",
      route = "intake_judge",
      decision = "enable",
    },
    service_class = "expedite",
  })
end

local function initial_event()
  return {
    queue = "devloop_execute_request",
    payload = execution_request(),
    source_ref = {
      kind = "external",
      reference = repo .. "#issue/" .. tostring(issue_number),
    },
  }
end

local function mock_env()
  for _ = 1, 16 do
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
  for _ = 1, 8 do
    t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
      stdout = "/tmp/fkst-packages-test/github-devloop-run-graph-execute-start/runtime",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_execute_start_issue()
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = repo,
    number = issue_number,
    title = "Add retry backoff to failed widget sync",
    body = "Implement exponential backoff for widget sync retries.",
    updated_at = "2026-06-03T01:02:03Z",
    state = "OPEN",
    labels = {},
    comments = {},
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }, "title,body,createdAt,updatedAt,labels,comments,state,assignees,author", 1)
end

local function mock_consensus_approval()
  for _ = 1, 3 do
    t.mock_command("mkdir -p", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("codex exec", {
      stdout = verdict_label .. " approve\n" .. reply_label .. " execute start approves.\n",
      stderr = "",
      exit_code = 0,
    })
  end
end

return {
  test_run_graph_execution_request_handoffs_to_execute_start = function()
    local request = execution_request()
    mock_env()
    mock_execute_start_issue()
    h.mock_context_bundle(request)
    mock_consensus_approval()

    local trace = graph.require_quiescent(graph.run(initial_event(), { max_steps = 8 }))
    graph.assert_covers(trace, {
      "github-devloop.devloop_execute_request -> github-devloop.execute_start",
      "consensus.proposal -> consensus.decide",
      "github-proxy.github_issue_comment_request -> github-proxy.github_comment",
      "github-proxy.github_issue_label_request -> github-proxy.github_issue_label",
    })

    local step = graph.require_delivery(trace, {
      queue = "github-devloop.devloop_execute_request",
      consumer = "github-devloop.execute_start",
    })
    t.eq(step.exit_code, 0)
    t.eq(#step.raises, 3)
    t.eq(step.raises[1].queue, "github-proxy.github_issue_comment_request")
    t.eq(step.raises[2].queue, "github-proxy.github_issue_label_request")
    t.eq(step.raises[3].queue, "consensus.proposal")

    local comment = step.raises[1].payload
    t.eq(comment.schema, "github-proxy.v1")
    t.eq(comment.repo, repo)
    t.eq(tostring(comment.issue_number), tostring(issue_number))
    t.is_true(comment.body:find(core.state_marker(proposal_id, "thinking", request_dedup_key), 1, true) ~= nil)

    local label = step.raises[2].payload
    t.eq(label.schema, "github-proxy.label.v1")
    t.eq(label.add_labels[1], "fkst-dev:thinking")
    t.eq(label.dedup_key, request_dedup_key .. "/label/thinking")

    local proposal = step.raises[3].payload
    t.eq(proposal.schema, "consensus.proposal.v1")
    t.eq(proposal.proposal_id, proposal_id)
    t.eq(proposal.dedup_key, request_dedup_key)
    t.eq(proposal.effect_version, request_dedup_key)
    t.eq(proposal.intake_hand_off.kind, "own-intake-decision")
    t.eq(proposal.intake_hand_off.dedup_key, request_dedup_key)
    t.eq(proposal.source_ref.ref, repo .. "#issue/" .. tostring(issue_number))
  end,
}
