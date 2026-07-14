local h = require("tests.devloop_helpers")
local graph = require("testkit.graph")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local config = require("devloop.config")

local t = h.t
local core = h.core

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
end

local function state_marker()
  return core.state_marker(
    "github-devloop/issue/owner/repo/42",
    "thinking",
    "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
  )
end

local function mock_runtime_and_context()
  for _ = 1, 24 do
    t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
      stdout = "/tmp/fkst-packages-test/github-devloop-run-graph/runtime",
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
      stdout = '{"labels":[{"name":"fkst-dev:thinking"}],"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_github_proxy_writes()
  for _ = 1, 2 do
    t.mock_command("gh api --paginate --slurp repos/owner/repo/issues/42/comments?per_page=100", {
      stdout = "[[]]\n",
      stderr = "",
      exit_code = 0,
    })
  end
  for comment_id = 123456, 123457 do
    t.mock_command("gh api --method POST repos/owner/repo/issues/42/comments --field 'body=", {
      stdout = '{"id":' .. tostring(comment_id) .. ',"body":"created","user":{"login":"fkst-test-bot"}}\n',
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command("gh label list --repo owner/repo --limit 1000 --json name", {
    stdout = '[{"name":"fkst-dev:thinking"},{"name":"fkst-dev:blocked"}]\n',
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh issue edit 42 --repo owner/repo", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_reads()
  entity_read_mocks.mock_issue_read_with_defaults(
    t,
    { "fkst-dev:thinking" },
    { state_marker() },
    {
      repo = "owner/repo",
      number = 42,
      title = "Implement decision recorder",
      updated_at = "2026-06-03T01:02:03Z",
      state = "OPEN",
    }
  )
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = "owner/repo",
    number = 42,
    title = "Implement decision recorder",
    updated_at = "2026-06-03T01:02:03Z",
    state = "OPEN",
    labels = { "fkst-dev:thinking" },
    comments = { state_marker() },
  }, "title,updatedAt,labels,comments,state")
  t.mock_command("gh issue view 42 --repo owner/repo --json 'title,updatedAt,labels,comments,state'", {
    stdout = entity_read_mocks.issue_view_stdout({
      repo = "owner/repo",
      number = 42,
      title = "Implement decision recorder",
      updated_at = "2026-06-03T01:02:03Z",
      state = "OPEN",
      labels = { "fkst-dev:thinking" },
      comments = { state_marker() },
    }),
    stderr = "",
    exit_code = 0,
  })
end

local function unresolved_at_cap()
  local cap = config.max_converge_rounds(core)
  return {
    schema = "consensus.consensus_converge.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/loop/" .. tostring(cap),
    source_ref = source_ref(),
    round = cap,
    narrowed_question = "Question " .. tostring(cap),
    angle_digests = {
      { angle = "minimal", verdict = "abstain", digest = "still-blocked" },
    },
  }
end

local function initial_event()
  return {
    queue = "consensus.consensus_converge",
    payload = unresolved_at_cap(),
    source_ref = {
      kind = "external",
      reference = "owner/repo#issue/42",
    },
  }
end

return {
  test_run_graph_no_consensus_handoffs_reconcile_to_blocked = function()
    mock_runtime_and_context()
    mock_issue_reads()
    mock_github_proxy_writes()

    local trace = graph.require_quiescent(graph.run(initial_event(), { max_steps = 8 }))
    graph.assert_covers(trace, {
      "consensus.consensus_converge -> github-devloop.loop",
      "github-proxy.github_issue_comment_request -> github-proxy.github_comment",
      "github-proxy.github_comment_written -> github-devloop.comment_handoff",
      "github-proxy.github_issue_label_request -> github-proxy.github_issue_label",
    })

    local loop_step, loop_index = graph.require_delivery(trace, {
      queue = "consensus.consensus_converge",
      consumer = "github-devloop.loop",
    })
    t.eq(loop_step.exit_code, 0)

    local written, _, written_index = graph.require_raise(
      trace,
      "github-proxy.github_comment_written",
      function(raised)
        return raised.payload.handoff ~= nil
          and raised.payload.handoff.kind == "github-devloop.reconcile"
      end
    )
    t.is_true(written_index > loop_index)
    t.is_true(written.payload.dedup_key:find("/written/", 1, true) ~= nil)

    local reconcile, _, reconcile_index = graph.require_raise(trace, "github-devloop.devloop_reconcile")
    t.is_true(reconcile_index > written_index)
    t.eq(reconcile.payload.schema, "github-devloop.reconcile.v1")

    local blocked_comment, _, blocked_index = graph.require_raise(
      trace,
      "github-proxy.github_issue_comment_request",
      function(raised)
        return graph.payload_contains(raised, "github-devloop reconcile action: drop")
          and graph.payload_contains(raised, "no-actionable-framing-after-")
          and graph.payload_contains(raised, 'state="blocked"')
      end
    )
    t.is_true(blocked_index > reconcile_index)
    t.is_true(blocked_comment.payload.body:find("fkst:github-devloop:reconcile:v1", 1, true) ~= nil)

    graph.require_raise(trace, "github-proxy.github_issue_label_request", function(raised)
      return raised.payload.add_labels[1] == "fkst-dev:blocked"
    end)
  end,
}
