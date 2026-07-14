local h = require("tests.proxy_integration_helpers")
local t = h.t
local opts = h.opts
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_repo_label_list = h.mock_repo_label_list
local count_calls = h.count_calls

local added_label = "adapter-reviewing"
local removed_label = "adapter-pr-open"
local marker_current = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="reviewing" version="v1" stage_rank="675" -->'
local marker_superseded = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="merge-ready" version="v1" stage_rank="725" -->'
local marker_current_timestamp = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="reviewing" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z" stage_rank="675" -->'
local marker_newer_same_state = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="reviewing" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T23-18-19Z" stage_rank="675" -->'
local marker_stale_merge_ready = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="merge-ready" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z" stage_rank="690" -->'
local marker_newer_reviewing = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="reviewing" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T23-18-19Z" stage_rank="675" -->'
local marker_fix_9 = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="fixing" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z/fix/9" stage_rank="700" -->'
local marker_fix_10 = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="fixing" version="ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z/fix/10" stage_rank="700" -->'

local function label_event(extra)
  local payload = {
    schema = "github-proxy.label.v1",
    repo = "owner/x",
    target_kind = "pr",
    target_number = 7,
    pr_number = 7,
    issue_number = 42,
    add_labels = { added_label },
    remove_labels = { removed_label },
    dedup_key = "adapter/issue/owner/x/42/pr-label/reviewing/v1/7",
    source_ref = {
      kind = "external",
      ref = "owner/x#pr/7",
    },
    claim = {
      owner = "fkst-test-bot",
      source_ref = {
        kind = "external",
        ref = "owner/x#issue/42",
      },
    },
  }
  for key, value in pairs(extra or {}) do
    payload[key] = value
  end
  return {
    queue = "github_issue_label_request",
    payload = payload,
  }
end

local function guarded_label_event(extra)
  local payload = {
    require_marker_guard = true,
    marker_guard = {
      namespace = "generic-workflow",
      marker = "state",
      version = "v1",
      match = {
        proposal = "generic-workflow/issue/owner/x/42",
      },
      expected = {
        state = "reviewing",
        version = "v1",
      },
      order_by = {
        "stage_rank",
        "version_order_key",
      },
    },
    expected_proposal_id = "generic-workflow/issue/owner/x/42",
    expected_state = "reviewing",
    expected_version = "v1",
  }
  for key, value in pairs(extra or {}) do
    payload[key] = value
  end
  return label_event(payload)
end

local function mock_pr_comment_view(comments)
  local parts = {}
  for index, body in ipairs(comments or {}) do
    table.insert(parts, string.format(
      '{"id":%d,"body":"%s","user":{"login":"fkst-test-bot"}}',
      index,
      h.json_string(body)
    ))
  end
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100", {
    stdout = "[[" .. table.concat(parts, ",") .. "]]\n",
    stderr = "",
    exit_code = 0,
  })
end

return {
  test_pr_label_request_skips_plain_pr_label_without_marker_guard = function()
    mock_write_env("1")
    mock_bot_env()

    local result = t.run_department("departments/github_issue_label/main.lua", label_event(), opts("pr-label-write", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api repos/owner/x/pulls/7"), 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 0)
    t.eq(count_calls("gh api repos/owner/x/issues/42"), 0)
    t.eq(count_calls("gh pr edit"), 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_pr_label_request_skips_when_generic_marker_guard_absent = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({})
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event(), opts("pr-label-guard-absent", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh label list"), 0)
    t.eq(count_calls("gh pr edit"), 0)
  end,

  test_pr_label_request_applies_when_generic_marker_guard_current = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({ marker_current })
    mock_repo_label_list({ added_label, removed_label })
    t.mock_command("gh pr edit", { stdout = "", stderr = "", exit_code = 0 })
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event(), opts("pr-label-guard-current", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh pr edit"), 1)
  end,

  test_pr_label_request_skips_when_generic_marker_guard_is_superseded = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({ marker_current, marker_superseded })
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event(), opts("pr-label-guard-superseded", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh label list"), 0)
    t.eq(count_calls("gh pr edit"), 0)
  end,

  test_pr_label_request_skips_when_generic_marker_guard_version_is_superseded = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({ marker_current_timestamp, marker_newer_same_state })
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event({
      marker_guard = {
        namespace = "generic-workflow",
        marker = "state",
        version = "v1",
        match = {
          proposal = "generic-workflow/issue/owner/x/42",
        },
        expected = {
          state = "reviewing",
          version = "ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z",
        },
        order_by = {
          "stage_rank",
          "version_order_key",
        },
      },
    }), opts("pr-label-guard-version-superseded", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh label list"), 0)
    t.eq(count_calls("gh pr edit"), 0)
  end,

  test_pr_label_request_skips_stale_higher_stage_marker_when_version_order_is_newer = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({ marker_stale_merge_ready, marker_newer_reviewing })
    mock_repo_label_list({ "adapter-merge-ready", "adapter-reviewing" })
    t.mock_command("gh pr edit", { stdout = "", stderr = "", exit_code = 0 })
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event({
      add_labels = { "adapter-merge-ready" },
      remove_labels = { "adapter-reviewing" },
      marker_guard = {
        namespace = "generic-workflow",
        marker = "state",
        version = "v1",
        match = {
          proposal = "generic-workflow/issue/owner/x/42",
        },
        expected = {
          state = "merge-ready",
          version = "ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z",
        },
        order_by = {
          "version_order_key",
          "stage_rank",
        },
      },
    }), opts("pr-label-guard-version-newer-lower-stage", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh label list"), 0)
    t.eq(count_calls("gh pr edit"), 0)
  end,

  test_pr_label_request_skips_stale_fix_9_when_fix_10_marker_is_current = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({ marker_fix_9, marker_fix_10 })
    t.mock_command("gh api repos/owner/x/issues/42", {
      stdout = '{"assignees":[{"login":"fkst-test-bot"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_issue_label/main.lua", guarded_label_event({
      add_labels = { "adapter-fixing" },
      remove_labels = { "adapter-reviewing" },
      marker_guard = {
        namespace = "generic-workflow",
        marker = "state",
        version = "v1",
        match = {
          proposal = "generic-workflow/issue/owner/x/42",
        },
        expected = {
          state = "fixing",
          version = "ready/consensus-generic-workflow/issue/owner/x/42/2026-06-17T22-18-19Z/fix/9",
        },
        order_by = {
          "version_order_key",
          "stage_rank",
        },
      },
    }), opts("pr-label-guard-fix-9-stale", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh label list"), 0)
    t.eq(count_calls("gh pr edit"), 0)
  end,
}
