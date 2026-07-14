local h = require("tests.proxy_integration_helpers")
local conformance = require("testkit.namespaced_dispatch_conformance")
local t = h.t

local function load_department(path, module_name)
  local old_pipeline = pipeline
  local module = require(module_name)
  pipeline = old_pipeline
  return { path = path, module = module }
end

local departments = conformance.loaded_departments({
  load_department("departments/github_comment/main.lua", "departments.github_comment.main"),
  load_department("departments/github_issue_blocked_by/main.lua", "departments.github_issue_blocked_by.main"),
  load_department("departments/github_issue_create/main.lua", "departments.github_issue_create.main"),
  load_department("departments/github_issue_label/main.lua", "departments.github_issue_label.main"),
  load_department("departments/github_poll/main.lua", "departments.github_poll.main"),
  load_department("departments/github_pr_comment/main.lua", "departments.github_pr_comment.main"),
  load_department("departments/test_entity_view_probe/main.lua", "departments.test_entity_view_probe.main"),
})

local function source_ref(ref)
  return {
    kind = "external",
    ref = ref or "owner/x#issue/42",
  }
end

local function issue_comment_payload()
  return {
    schema = "github-proxy.v1",
    repo = "owner/x",
    issue_number = 42,
    body = "state marker",
    dedup_key = "github-proxy/namespaced/issue-comment",
    source_ref = source_ref(),
  }
end

local function pr_comment_payload()
  return {
    schema = "github-proxy.v1",
    repo = "owner/x",
    pr_number = 7,
    issue_number = 42,
    body = "review marker",
    dedup_key = "github-proxy/namespaced/pr-comment",
    source_ref = source_ref("owner/x#pr/7"),
  }
end

local function issue_create_payload()
  return {
    schema = "github-proxy.v1",
    repo = "owner/x",
    title = "Namespaced dispatch probe",
    body = "Small issue-create dry-run probe.",
    dedup_key = "github-proxy/namespaced/issue-create",
    labels = {},
    assignees = {},
    source_ref = source_ref("owner/x#issue/42"),
  }
end

local function label_payload()
  return {
    schema = "github-proxy.label.v1",
    repo = "owner/x",
    target_kind = "issue",
    issue_number = 42,
    add_labels = { "adapter-enabled" },
    remove_labels = {},
    dedup_key = "github-proxy/namespaced/label",
    source_ref = source_ref(),
  }
end

local function blocked_by_payload()
  return {
    schema = "github-proxy.issue-blocked-by.v1",
    repo = "owner/x",
    blocked_issue_number = 42,
    blocking_issue_number = 99,
    dedup_key = "github-proxy/namespaced/blocked-by",
    source_ref = source_ref(),
  }
end

local function entity_view_probe_payload()
  return {
    repo = "owner/x",
    kind = "issue",
    number = 42,
    updated_at = "2026-06-03T01:02:03Z",
    consumer = "namespaced-dispatch",
  }
end

local function payload_for_queue(_path, queue)
  local payloads = {
    entity_view_probe = entity_view_probe_payload(),
    github_issue_blocked_by_request = blocked_by_payload(),
    github_issue_comment_request = issue_comment_payload(),
    github_issue_create_request = issue_create_payload(),
    github_issue_label_request = label_payload(),
    github_poll_tick = { schema = "github-proxy.poll-tick.v1" },
    github_pr_comment_request = pr_comment_payload(),
  }
  local payload = payloads[queue]
  if payload == nil then
    error("github-proxy: no production-shaped queue fixture for " .. tostring(queue))
  end
  return payload
end

local function mock_env()
  h.mock_repo_env("owner/x")
  h.mock_write_env("")
  h.mock_proxy_replay_budget_env("")
end

local function mock_issue_view(title)
  t.mock_command("gh api repos/owner/x/issues/42", {
    stdout = '{"title":"' .. tostring(title) .. '","body":"","state":"open","labels":[],"assignees":[],"updated_at":"2026-06-03T01:02:03Z"}\n',
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
    stdout = "[[]]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_for_case(_path, queue)
  mock_env()
  if queue == "github_poll_tick" then
    h.mock_poll()
  elseif queue == "entity_view_probe" then
    mock_issue_view("Namespaced probe")
  end
end

local function opts_for_case(path, queue)
  mock_for_case(path, queue)
  return {
    run_opts = {
      env = {
        FKST_RUNTIME_ROOT = "/tmp/fkst-packages-test/github-proxy/namespaced-" .. tostring(queue):gsub("[^%w._-]", "_"),
        FKST_GITHUB_REPO = "owner/x",
        FKST_GITHUB_WRITE = "",
        FKST_GITHUB_PROXY_REPLAY_BUDGET = "",
      },
    },
    before_replay = function()
      mock_for_case(path, queue)
    end,
  }
end

return {
  test_all_departments_accept_production_namespaced_consumed_queues = function()
    conformance.assert_all_consumed_queues_route({
      t = t,
      package_name = "github-proxy",
      package_root = "packages/github-proxy",
      departments = departments,
      payload_for_queue = payload_for_queue,
      opts_for_case = opts_for_case,
    })
  end,
}
