local h = require("tests.devloop_helpers")
local graph = require("testkit.graph")
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local t = h.t
local core = h.core

local proposal_id = "github-devloop/issue/owner/repo/42"
local consensus_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local ready_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
end

local function state_marker(state, version)
  return core.state_marker(proposal_id, state, version)
end

local function blocked_by_json()
  return '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":0,"pageInfo":{"hasNextPage":false},"nodes":[]}}}}}\n'
end

local function mock_empty_dependencies()
  t.mock_command(core.gh_blocked_by_cmd("owner/repo", 42), {
    stdout = blocked_by_json(),
    stderr = "",
    exit_code = 0,
  })
end

local function reached()
  return {
    schema = "consensus.consensus_reached.v1",
    proposal_id = proposal_id,
    decision = "approve",
    body = "All angles approve.",
    dedup_key = consensus_version,
    source_ref = source_ref(),
  }
end

local function initial_event()
  return {
    queue = "consensus.consensus_reached",
    payload = reached(),
    source_ref = {
      kind = "external",
      reference = "owner/repo#issue/42",
    },
  }
end

local function mock_runtime_and_context()
  for _ = 1, 24 do
    t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
      stdout = "/tmp/fkst-packages-test/github-devloop-run-graph-ready/runtime",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 32 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = "1",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 4 do
    t.mock_command("gh api repos/owner/repo/issues/42", {
      stdout = '{"labels":[{"name":"fkst-dev:ready"}],"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_github_proxy_comment_write()
  for _, command in ipairs({
    "gh api --paginate --slurp repos/owner/repo/issues/42/comments?per_page=100",
    "gh api --paginate --slurp 'repos/owner/repo/issues/42/comments?per_page=100'",
  }) do
    t.mock_command(command, {
      stdout = "[[]]\n",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 2 do
    t.mock_command("gh api --method POST repos/owner/repo/issues/42/comments --field 'body=", {
      stdout = '{"id":123456,"body":"created","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_label_write()
  t.mock_command("gh label list --repo owner/repo --limit 1000 --json name", {
    stdout = '[{"name":"fkst-dev:thinking"},{"name":"fkst-dev:ready"}]\n',
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh issue edit 42 --repo owner/repo", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_consensus_result_issue_read()
  entity_read_mocks.mock_issue_read_with_defaults(
    t,
    { "fkst-dev:thinking" },
    { state_marker("thinking", consensus_version) },
    {
      repo = "owner/repo",
      number = 42,
      title = "Implement decision recorder",
      updated_at = "2026-06-03T01:02:03Z",
      state = "OPEN",
      times = 1,
    }
  )
end

local function mock_implement_issue_read()
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = "owner/repo",
    number = 42,
    title = "Implement decision recorder",
    updated_at = "2026-06-03T01:02:03Z",
    state = "CLOSED",
    labels = { "fkst-dev:ready" },
    comments = { state_marker("ready", ready_version) },
  }, "title,body,comments,labels,state,createdAt,updatedAt,assignees,author", 1)
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = "owner/repo",
    number = 42,
    title = "Implement decision recorder",
    updated_at = "2026-06-03T01:02:03Z",
    state = "CLOSED",
    labels = { "fkst-dev:ready" },
    comments = { state_marker("ready", ready_version) },
  }, "title,body,labels,comments,state,author", 1)
end

return {
  test_run_graph_consensus_reached_handoffs_ready_to_implement = function()
    mock_runtime_and_context()
    mock_empty_dependencies()
    mock_consensus_result_issue_read()
    mock_github_proxy_comment_write()
    mock_label_write()
    mock_implement_issue_read()

    local trace = graph.require_quiescent(graph.run(initial_event(), { max_steps = 8 }))
    graph.assert_covers(trace, {
      "consensus.consensus_reached -> github-devloop.consensus_result",
      "github-proxy.github_issue_comment_request -> github-proxy.github_comment",
      "github-proxy.github_comment_written -> github-devloop.comment_handoff",
    })

    local result_step, result_index = graph.require_delivery(trace, {
      queue = "consensus.consensus_reached",
      consumer = "github-devloop.consensus_result",
    })
    t.eq(result_step.exit_code, 0)

    local ready_request, _, ready_request_index = graph.require_raise(
      trace,
      "github-proxy.github_issue_comment_request",
      function(raised)
        return graph.payload_contains(raised, 'state="ready"')
          and raised.payload.handoff ~= nil
        and raised.payload.handoff.kind == "github-devloop.ready"
      end
    )
    t.eq(ready_request_index, result_index)
    t.eq(ready_request.payload.handoff.proposal_id, proposal_id)
    t.eq(ready_request.payload.handoff.marker_version, consensus_version)

    local written, _, written_index = graph.require_raise(
      trace,
      "github-proxy.github_comment_written",
      function(raised)
        return raised.payload.handoff ~= nil
          and raised.payload.handoff.kind == "github-devloop.ready"
      end
    )
    t.is_true(written_index > ready_request_index)
    t.is_true(written.payload.dedup_key:find("/written/", 1, true) ~= nil)

    local ready, _, ready_index = graph.require_raise(trace, "github-devloop.devloop_ready")
    t.is_true(ready_index > written_index)
    t.eq(ready.payload.schema, "github-devloop.ready.v1")
    t.eq(ready.payload.proposal_id, proposal_id)
    t.eq(ready.payload.dedup_key, ready_version)
    t.eq(ready.payload.ready_hand_off.comment_id, "123456")

    local implement_step, implement_index = graph.require_delivery(trace, {
      queue = "github-devloop.devloop_ready",
      consumer = "github-devloop.implement",
    })
    t.eq(implement_step.exit_code, 0)
    t.is_true(implement_index > ready_index)
  end,
}
