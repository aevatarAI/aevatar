local conformance = require("testkit.saga_conformance")
local forge_conformance = require("forge.saga_conformance")
local t = fkst.test

local function run_write()
  exec_sync({
    cmd = "gh issue comment 42 --repo owner/x --body-file /tmp/std-saga.md",
    timeout = 30,
  })
end

local function run_read()
  exec_sync({
    cmd = "gh issue view '42' --repo owner/x --json title",
    timeout = 30,
  })
end

local function run_git_push()
  exec_sync({
    cmd = "git -C '/tmp/std-saga-worktree' push origin HEAD:branch",
    timeout = 30,
  })
end

return {
  test_assert_external_effect_saga_requires_declared_post_conditions = function()
    local saga_def = {
      id = "test.external",
      steps = {
        {
          id = "write-comment",
          effect = "github.issue.comment",
          request_queue = "github_issue_comment_request",
          post_conditions = {
            {
              id = "trusted-marker-visible",
              kind = "trusted-comment-marker",
              marker = "<!-- fkst:test -->",
            },
          },
        },
      },
    }

    conformance.assert_external_effect_saga(saga_def)
  end,

  test_assert_external_effect_saga_rejects_steps_without_post_conditions = function()
    local ok, err = pcall(function()
      conformance.assert_external_effect_saga({
        id = "test.external",
        steps = {
          {
            id = "write-comment",
            effect = "github.issue.comment",
            request_queue = "github_issue_comment_request",
          },
        },
      })
    end)

    t.eq(ok, false)
    t.eq(tostring(err):find("step write-comment requires non-empty post_conditions", 1, true) ~= nil, true)
  end,

  test_assert_external_effect_post_condition_requires_trusted_marker_evidence = function()
    local ok, err = pcall(function()
      conformance.assert_external_effect_post_condition({
        id = "trusted-marker-visible",
        kind = "trusted-comment-marker",
        marker = "<!-- fkst:test -->",
      }, {})
    end)

    t.eq(ok, false)
    t.eq(tostring(err):find("requires marker body evidence", 1, true) ~= nil, true)
  end,

  test_write_class_classifier_is_explicit = function()
    t.eq(forge_conformance.is_write_class({ argv = { "gh", "issue", "comment", "42", "--repo", "owner/x" } }), true)
    t.eq(forge_conformance.is_write_class({ argv = { "gh", "issue", "view", "42", "--repo", "owner/x" } }), false)
    t.eq(forge_conformance.is_write_class({
      argv = { "gh", "api", "graphql" },
      stdin = "mutation { addLabelsToLabelable(input: {}) { clientMutationId } }",
    }), true)
    t.eq(forge_conformance.is_write_class({
      argv = { "gh", "api", "graphql" },
      stdin = "query { viewer { login } }",
    }), false)
    t.eq(forge_conformance.is_write_class({ argv = { "git", "-C", "/tmp/std-saga-worktree", "push", "origin", "HEAD:branch" } }), true)
    t.eq(forge_conformance.is_write_class({ argv = { "git", "-C", "/tmp/std-saga-worktree", "status", "--short" } }), false)
    t.eq(forge_conformance.is_write_class("gh issue comment 42 --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh issue reopen '42' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh pr merge '7' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh pr close '7' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh pr ready '7' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh pr reopen '7' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh label create 'adapter-ready' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("gh workflow run 'ci.yml' --repo owner/x"), true)
    t.eq(forge_conformance.is_write_class("git push origin HEAD:branch"), true)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' push origin HEAD:branch"), true)
    t.eq(forge_conformance.is_write_class("gh api --method POST repos/owner/x/issues/42/comments"), true)
    t.eq(forge_conformance.is_write_class("gh api graphql\nmutation { addLabelsToLabelable(input: {}) { clientMutationId } }"), true)
    t.eq(forge_conformance.is_write_class("gh issue view '42' --repo owner/x"), false)
    t.eq(forge_conformance.is_write_class("gh pr diff '7' --repo owner/x"), false)
    t.eq(forge_conformance.is_write_class("gh api repos/owner/x/issues/42"), false)
    t.eq(forge_conformance.is_write_class("gh api graphql\nquery { viewer { login } }"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' log --oneline"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' show HEAD"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' rev-parse HEAD"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' status --short"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' diff --stat"), false)
    t.eq(forge_conformance.is_write_class("git -C '/tmp/std-saga-worktree' cat-file -t HEAD"), false)
  end,

  test_assert_progress_passes_when_first_writes = function()
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    conformance.assert_progress(t, {
      first = run_write,
      is_write_class = forge_conformance.is_write_class,
    })
  end,

  test_assert_progress_fails_when_first_only_reads = function()
    t.mock_command("gh issue view '42'", {
      stdout = "{}",
      stderr = "",
      exit_code = 0,
    })

    t.raises(function()
      conformance.assert_progress(t, {
        first = run_read,
        is_write_class = forge_conformance.is_write_class,
      })
    end)
  end,

  test_assert_idempotent_passes_when_second_only_reads = function()
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue view '42'", {
      stdout = "{}",
      stderr = "",
      exit_code = 0,
    })

    conformance.assert_idempotent(t, {
      first = run_write,
      second = run_read,
      is_write_class = forge_conformance.is_write_class,
    })
  end,

  test_assert_idempotent_fails_when_second_writes = function()
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local ok, err = pcall(function()
      conformance.assert_idempotent(t, {
        first = run_write,
        second = run_write,
        is_write_class = forge_conformance.is_write_class,
      })
    end)
    t.eq(ok, false)
    t.eq(tostring(err):find("observed effects on second delivery", 1, true) ~= nil, true)
  end,

  test_assert_idempotent_fails_when_second_errors_before_writing = function()
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    t.raises(function()
      conformance.assert_idempotent(t, {
        first = run_write,
        second = function()
          error("replay exploded before write")
        end,
        is_write_class = forge_conformance.is_write_class,
      })
    end)

    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local ok, err = pcall(function()
      conformance.assert_idempotent(t, {
        first = run_write,
        second = function()
          error("replay exploded before write")
        end,
        is_write_class = forge_conformance.is_write_class,
      })
    end)
    t.eq(ok, false)
    t.eq(tostring(err):find("second delivery errored; idempotent no-op not proven", 1, true) ~= nil, true)
  end,

  test_assert_idempotent_fails_when_second_git_c_pushes = function()
    t.mock_command("gh issue comment 42", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("git -C '/tmp/std-saga-worktree' push", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local ok, err = pcall(function()
      conformance.assert_idempotent(t, {
        first = run_write,
        second = run_git_push,
        is_write_class = forge_conformance.is_write_class,
      })
    end)
    t.eq(ok, false)
    t.eq(tostring(err):find("observed effects on second delivery", 1, true) ~= nil, true)
  end,
}
