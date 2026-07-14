local h = require("tests.devloop_helpers")
local saga_conformance = require("testkit.saga_conformance")
local forge_saga_conformance = require("forge.saga_conformance")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = h.t
local core = h.core
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local two_issue_json = [[{"issues":[{"title":"Extract retry helper","body":"Smaller scope: implement retry helper.\nNon-goals: no workflow rewrite.\nAcceptance: helper tests pass."},{"title":"Wire retry helper","body":"Smaller scope: wire one caller.\nNon-goals: no unrelated states.\nAcceptance: integration test passes."}]}]]
local first_delivery_facts = nil

local function blocked_comments(event, extra)
  local comments = {
    m_builders.pr_origin_marker(core, event.proposal_id, "42", "devloop-owner-repo-42-01HY", event.version, "dev"),
    core.state_marker(event.proposal_id, "blocked", event.version),
    conv_reconcile.fix_reconcile_marker(core, event.proposal_id, event.version, "drop"),
  }
  for _, comment in ipairs(extra or {}) do
    table.insert(comments, comment)
  end
  return comments
end

local function mock_write_env_real()
  for _ = 1, 4 do
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = "1",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_child_issue_list(event, indexes)
  local rendered = {}
  for _, index in ipairs(indexes or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"title":"Child %d","state":"OPEN","author":{"login":"fkst-test-bot"},"body":"%s","url":"https://github.example/owner/repo/issues/%d"}',
      100 + index,
      index,
      h.json_string(decompose_lib.decompose_child_marker(core, event.proposal_id, event.version, event.pr_number, index)),
      100 + index
    ))
  end
  t.mock_command(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_child_issue_list_from_bodies(event, bodies)
  local rendered = {}
  for index, body in ipairs(bodies or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"title":"Child %d","state":"OPEN","author":{"login":"fkst-test-bot"},"body":"%s","url":"https://github.example/owner/repo/issues/%d"}',
      100 + index,
      index,
      h.json_string(body),
      100 + index
    ))
  end
  t.mock_command(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_child_issue_list_repeated(event, indexes, times)
  for _ = 1, times do
    mock_child_issue_list(event, indexes)
  end
end

local function mock_pr_view(event, comments)
  local selected = {
    m_builders.pr_origin_marker(core, event.proposal_id, "42", "devloop-owner-repo-42-01HY", event.version, "dev"),
  }
  for _, comment in ipairs(comments) do
    table.insert(selected, comment)
  end
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = selected,
    head = "devloop-owner-repo-42-01HY",
    head_sha = "def456",
    base_branch = "dev",
    state = "OPEN",
  }, entity_read_mocks.pr_origin_selector)
end

local function pr_comment_body_path()
  for _, call in ipairs(t.command_calls()) do
    if gh_argv.call_contains(call, "gh pr comment") then
      return gh_argv.argv_value_after(call, "--body-file")
    end
  end
  return nil
end

local function capture_first_delivery_facts(result)
  local path = pr_comment_body_path()
  if path == nil then
    error("decompose saga test: first delivery did not write decomposed marker")
  end
  local child_bodies = {}
  for _, raised in ipairs(result.raises or {}) do
    if raised.queue == "github-proxy.github_issue_create_request" then
      table.insert(child_bodies, raised.payload.body)
    end
  end
  if #child_bodies == 0 then
    error("decompose saga test: first delivery did not raise child issue creates")
  end
  first_delivery_facts = {
    marker_body = file.read(path),
    child_bodies = child_bodies,
  }
end

local function mock_decompose_codex(stdout)
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 2 do
    t.mock_command("test -d", { stdout = "", stderr = "", exit_code = 1 })
  end
  t.mock_command("install -d -m 0755", { stdout = "", stderr = "", exit_code = 0 })
  t.mock_command("mktemp -d", {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime/context/.bundle-tmp.decompose\n",
    stderr = "",
    exit_code = 0,
  })
  entity_read_mocks.mock_issue_view_raw_selector(t, {}, "title,body,updatedAt,labels,comments,state", {
    stdout = '{"title":"Original large issue","body":"Original body","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[{"name":"fkst-dev:blocked"}],"comments":[]}\n',
  })
  entity_read_mocks.mock_pr_view_raw_selector(t, {}, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels", {
    stdout = '{"title":"PR title","body":"PR body","headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"OPEN","updatedAt":"2026-06-04T01:02:03Z","comments":[],"labels":[]}\n',
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
  for _ = 1, 6 do
    t.mock_command(" > ", { stdout = "", stderr = "", exit_code = 0 })
  end
  t.mock_command("python3 -c", { stdout = "", stderr = "", exit_code = 0 })
  t.mock_command("test -r", { stdout = "", stderr = "", exit_code = 0 })
  for _ = 1, 8 do
    t.mock_command("wc -c < ", { stdout = "1\n", stderr = "", exit_code = 0 })
  end
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("mkdir -p", { stdout = "", stderr = "", exit_code = 0 })
  t.mock_command("codex exec", {
    stdout = stdout,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_first_delivery(event)
  first_delivery_facts = nil
  h.mock_bot_env()
  mock_write_env_real()
  h.mock_default_issue_claim()
  h.mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
    title = "Original large issue",
    body = "Original body that describes too much scope.",
  })
  mock_pr_view(event, blocked_comments(event))
  mock_pr_view(event, blocked_comments(event))
  mock_decompose_codex(two_issue_json)
  t.mock_command("gh pr comment", { stdout = "", stderr = "", exit_code = 0 })
  mock_pr_view(event, blocked_comments(event, {
    core.decomposed_comment_body(event, 2),
  }))
  mock_child_issue_list(event, {})
end

local function mock_second_delivery(event)
  h.mock_bot_env()
  h.mock_default_issue_claim()
  if first_delivery_facts == nil then
    error("decompose saga test: first delivery facts were not captured")
  end
  mock_pr_view(event, blocked_comments(event, {
    first_delivery_facts.marker_body,
  }))
  mock_child_issue_list_from_bodies(event, first_delivery_facts.child_bodies)
end

local function run_decompose(event, name)
  return t.run_department("departments/decompose/main.lua", {
    queue = "devloop_decompose",
    payload = event,
  }, h.opts(name, {
    FKST_GITHUB_WRITE = "1",
    FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
  }))
end

return {
  test_123_decompose_saga_progress_and_idempotency = function()
    local event = h.decompose_event()
    saga_conformance.assert_progress(t, {
      is_write_class = forge_saga_conformance.is_write_class,
      first = function()
        mock_first_delivery(event)
        return run_decompose(event, "decompose-saga-progress")
      end,
    })

    saga_conformance.assert_idempotent(t, {
      is_write_class = forge_saga_conformance.is_write_class,
      first = function()
        mock_first_delivery(event)
        local result = run_decompose(event, "decompose-saga-first")
        capture_first_delivery_facts(result)
        return result
      end,
      second = function()
        mock_second_delivery(event)
        return run_decompose(event, "decompose-saga-second")
      end,
    })
  end,
}
