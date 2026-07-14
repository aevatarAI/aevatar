local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core
local ready = h.ready
local run_implement = h.run_implement
local opts = h.opts
local mock_issue_implement = h.mock_issue_implement
local count_calls = h.count_calls
local find_raise = h.find_raise

local entry_key = "1111111111111111111111111111111111111111111111111111111111111111"
local canonical_issue = 41
local ledger_ref = "refs/fkst/migration-slices/" .. entry_key
local ledger_sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

local function ledger_body(issue_number)
  return "tree 0000000000000000000000000000000000000000\n\n"
    .. '{"schema":"fkst.ratchet-migration-slice-ledger.v1"'
    .. ',"state":"issue-created"'
    .. ',"entry_key":"' .. entry_key .. '"'
    .. ',"allowlist_path":"migration/saga-handler.allowlist"'
    .. ',"generation":1'
    .. ',"claim_owner":"fkst-test-bot"'
    .. ',"claimed_at":"2026-06-19T00:00:00Z"'
    .. ',"issue_number":' .. tostring(issue_number)
    .. ',"updated_at":"2026-06-19T00:00:00Z"}\n'
end

local function slice_body()
  return "Machine-filed ratchet slice issue.\n\n"
    .. '<!-- fkst:ratchet-slice:v1 schema="fkst.ratchet-slice.v1"'
    .. ' ratchet="saga-handler" parent="979" dedup="saga-handler/slice/test"'
    .. ' fingerprint="abc123" allowlist_path="migration/saga-handler.allowlist"'
    .. ' entry_key="' .. entry_key .. '" generation="1" coord_ref="' .. ledger_ref .. '"'
    .. ' entries="' .. entry_key .. '" -->\n'
end

local function mock_ledger(issue_number)
  t.mock_command("git ls-remote origin " .. ledger_ref, {
    stdout = ledger_sha .. "\t" .. ledger_ref .. "\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch origin " .. ledger_ref, {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git cat-file -p " .. ledger_sha, {
    stdout = ledger_body(issue_number),
    stderr = "",
    exit_code = 0,
  })
end

local function find_duplicate_comment(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find("Duplicate migration slice for entry_key=" .. entry_key .. "; canonical is #" .. tostring(canonical_issue), 1, true) ~= nil
  end)
end

local function find_duplicate_label(raises)
  return find_raise(raises, "github-proxy.github_issue_label_request", function(payload)
    for _, label in ipairs(payload.add_labels or {}) do
      if label == "fkst:duplicate-slice" then
        return true
      end
    end
    return false
  end)
end

local function command_index(needle)
  for index, call in ipairs(t.command_calls()) do
    if tostring(call.rendered or ""):find(needle, 1, true) ~= nil then
      return index
    end
  end
  return nil
end

return {
  test_noncanonical_migration_slice_exits_before_implementation = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    }, {
      body = slice_body(),
    })
    mock_ledger(canonical_issue)

    local result = run_implement(event, opts("implement-duplicate-slice", {
      FKST_GITHUB_WRITE = "",
    }))

    t.eq(result.exit_code, 0)
    t.is_true(find_duplicate_comment(result.raises) ~= nil)
    t.is_true(find_duplicate_label(result.raises) ~= nil)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls("git worktree list"), 0)
    t.eq(count_calls("git -C"), 0)
    t.eq(count_calls("git fetch origin " .. ledger_ref), 1)
    t.is_true(command_index("git fetch origin " .. ledger_ref) < command_index("git cat-file -p " .. ledger_sha))
    t.eq(count_calls("gh issue close"), 0)
  end,
}
