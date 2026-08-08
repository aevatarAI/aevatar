local h = require("tests.devloop_ops_helpers")
local t = h.t
local core = h.core
local run_id = tostring({}):gsub("[^%w._-]", "_")
local runtime_roots = {}

local function run_opts(name)
  if runtime_roots[name] == nil then
    runtime_roots[name] = "/tmp/fkst-packages-test/github-devloop/dead-letter-" .. run_id .. "/" .. tostring(name)
  end
  return {
    env = {
      FKST_RUNTIME_ROOT = runtime_roots[name],
      FKST_GITHUB_REPO = "owner/repo",
      FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      FKST_GITHUB_WRITE = "",
    },
  }
end

local function capture_logs(event)
  local captured = {}
  local old_log = log

  log = {
    info = function(message)
      table.insert(captured, tostring(message))
    end,
    warn = function(message)
      table.insert(captured, tostring(message))
    end,
    error = function(message)
      table.insert(captured, tostring(message))
    end,
  }

  local ok, result = pcall(function()
    local old_pipeline = pipeline
    local module = require("departments.dead_letter.main")
    local run = module.pipeline or pipeline
    pipeline = old_pipeline
    if type(run) ~= "function" then
      error("github-devloop: dead-letter department pipeline missing")
    end
    run(event)
  end)

  log = old_log
  if not ok then
    error(result)
  end

  return captured
end

local function event(payload)
  return {
    queue = "dead_letter",
    payload = payload,
  }
end

local function find_raise(raises, queue)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue then
      return raised
    end
  end
  return nil
end

return {
  test_dead_letter_logs_delivery_identity = function()
    local logs = capture_logs(event({
        delivery_id = "delivery/v1/raised/queue/github-devloop-decompose.devloop_decompose/dept/github-devloop-decompose.decompose/01HY",
        queue = "github-devloop-decompose.devloop_decompose",
        dept = "github-devloop-decompose.decompose",
        source_ref = {
          kind = "external",
          ref = "owner/repo#issue/140",
        },
        dedup_key = "github-devloop/issue/owner/repo/140/2026-06-10T08-46-17Z",
        attempt = 3,
        error = "gh pr decomposed marker comment failed\nwhile writing marker",
    }))

    t.eq(#logs, 2)
    t.eq(
      logs[1],
      "github-devloop dept=dead_letter tag=DEAD_LETTER"
        .. " error_class=dead-letter"
        .. " fingerprint=" .. core.error_fingerprint("dead-letter", "github-devloop-decompose.devloop_decompose", "github-devloop-decompose.decompose", "gh pr decomposed marker comment failed\nwhile writing marker")
        .. " source_ref=external:owner/repo#issue/140"
        .. " attempt=3"
        .. " terminal=true"
        .. " delivery_id=delivery/v1/raised/queue/github-devloop-decompose.devloop_decompose/dept/github-devloop-decompose.decompose/01HY"
        .. " queue=github-devloop-decompose.devloop_decompose"
        .. " dead_dept=github-devloop-decompose.decompose"
        .. " source_ref=external:owner/repo#issue/140"
        .. " dedup_key=github-devloop/issue/owner/repo/140/2026-06-10T08-46-17Z"
        .. " attempt=3"
        .. " error=gh pr decomposed marker comment failed while writing marker"
    )
    t.is_true(logs[2]:find("dept=failure_triage", 1, true) ~= nil)
    t.is_true(logs[2]:find("reason=missing-fingerprint", 1, true) ~= nil)
  end,

  test_terminal_error_fact_raises_intent_deduped_issue_create = function()
    local fact = core.build_error_fact({
      queue = "github-devloop.devloop_fixing",
      error_class = "codex-failed",
      message = "codex failed at /tmp/fkst-a on abcdef1234567890",
      source_ref = { kind = "external", ref = "owner/repo#issue/140" },
      attempt = 4,
      terminal = true,
    })
    fact.error = "codex failed\n<!-- fkst:forged -->"
    fact.dept = "github-devloop.fix"

    local result = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("terminal-raise"))

    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, "github-proxy.github_issue_create_request")
    t.is_true(raised ~= nil)
    local request = raised.payload
    t.eq(request.schema, "github-proxy.issue-create.v1")
    t.eq(request.repo, "owner/repo")
    t.eq(request.dedup_key, core.failure_triage_dedup_key("owner/repo", fact.fingerprint))
    t.eq(request.parent_comment_target.repo, "owner/repo")
    t.eq(request.parent_comment_target.issue_number, "140")
    t.eq(request.source_ref.kind, "external")
    t.eq(request.source_ref.ref, "owner/repo#issue/140")
    t.is_true(request.title:find("Investigate L2 failure: codex-failed", 1, true) ~= nil)
    t.is_true(request.body:find("`fingerprint`: `" .. fact.fingerprint .. "`", 1, true) ~= nil)
    t.is_true(request.body:find("`terminal`: `true`", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:forged -->", 1, true) ~= nil)
  end,

  test_failure_triage_issue_neutralizes_untrusted_fact_fields = function()
    local fact = {
      source_repo = "owner/repo",
      parent_target = {
        repo = "owner/repo",
        issue_number = "140",
      },
      source_ref = {
        kind = "external",
        ref = "owner/repo#issue/140` <!-- fkst:source-ref -->",
      },
      error_class = "codex-failed",
      queue = "github-devloop.devloop_fixing` <!-- fkst:queue -->",
      fingerprint = "efp-1` <!-- fkst:fingerprint -->",
      attempt = 1,
      terminal = true,
      dead_queue = "dead_letter` <!-- fkst:dead -->",
      dept = "github-devloop.fix` <!-- fkst:dept -->",
      delivery_id = "delivery-1` <!-- fkst:delivery -->",
      message = "summary <!-- fkst:message -->",
    }

    local request = core.build_failure_triage_issue_create_request(fact, 1)

    t.eq(request.title:find("<!-- fkst:", 1, true), nil)
    t.eq(request.body:find("<!-- fkst:", 1, true), nil)
    t.is_true(request.title:find("&lt;!-- fkst:queue -->", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:fingerprint -->", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:source-ref -->", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:dept -->", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:delivery -->", 1, true) ~= nil)
    t.is_true(request.body:find("&lt;!-- fkst:message -->", 1, true) ~= nil)
    t.eq(request.body:find("`github-devloop.fix`", 1, true), nil)
    t.is_true(request.body:find("`github-devloop.fix' &lt;!-- fkst:dept -->`", 1, true) ~= nil)
  end,

  test_non_terminal_error_fact_raises_for_new_fingerprint_and_threshold_crossing = function()
    local fact = core.build_error_fact({
      queue = "github-devloop.devloop_reviewing",
      error_class = "gh-rate-limited",
      message = "secondary rate limit",
      source_ref = { kind = "external", ref = "owner/repo#issue/141" },
      attempt = 1,
      terminal = false,
    })

    local first = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("threshold"))
    local second = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("threshold"))
    local third = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("threshold"))

    t.eq(first.exit_code, 0)
    t.eq(second.exit_code, 0)
    t.eq(third.exit_code, 0)
    local new_fingerprint = find_raise(first.raises, "github-proxy.github_issue_create_request")
    t.is_true(new_fingerprint ~= nil)
    t.eq(new_fingerprint.payload.dedup_key, core.failure_triage_dedup_key("owner/repo", fact.fingerprint))
    t.eq(find_raise(second.raises, "github-proxy.github_issue_create_request"), nil)
    local threshold_crossing = find_raise(third.raises, "github-proxy.github_issue_create_request")
    t.is_true(threshold_crossing ~= nil)
    t.eq(threshold_crossing.payload.dedup_key, new_fingerprint.payload.dedup_key)
    t.is_true(threshold_crossing.payload.body:find("`observed_count`: `3`", 1, true) ~= nil)
  end,

  test_pr_source_ref_uses_parent_pr_ledger_target = function()
    local fact = core.build_error_fact({
      queue = "github-devloop.devloop_review",
      error_class = "parse-failed",
      message = "review payload parse failed",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
      attempt = 1,
      terminal = true,
    })

    local result = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("pr-source-ref"))

    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, "github-proxy.github_issue_create_request")
    t.is_true(raised ~= nil)
    t.eq(raised.payload.repo, "owner/repo")
    t.eq(raised.payload.parent_comment_target.repo, "owner/repo")
    t.eq(raised.payload.parent_comment_target.pr_number, "7")
    t.eq(raised.payload.source_ref.ref, "owner/repo#pr/7")
  end,

  test_cron_source_ref_triages_without_issue_parent_or_raise = function()
    local fact = {
      schema = "fkst.failure_fact.v1",
      queue = "github-devloop.devloop_branch_tick",
      dept = "github-devloop.sync_scan",
      error_class = "framework-child-nonzero",
      fingerprint = "framework-child-nonzero:sync-scan",
      error = "sync scan failed",
      source_ref = { kind = "cron", ref = "" },
      attempt = 5,
      terminal = true,
    }

    local result = t.run_department("departments/dead_letter/main.lua", event(fact), run_opts("cron-source-ref"))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
  end,
}
