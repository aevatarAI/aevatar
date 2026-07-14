local core = require("core")
local t = fkst.test

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
      error("consensus: dead-letter department pipeline missing")
    end
    run(event)
  end)

  log = old_log
  if not ok then
    error(result)
  end

  return captured
end

return {
  test_dead_letter_logs_delivery_identity = function()
    local logs = capture_logs({
      queue = "dead_letter",
      payload = {
        delivery_id = "delivery/v1/raised/queue/consensus.proposal/dept/consensus.decide/01HY",
        queue = "consensus.proposal",
        dept = "consensus.decide",
        error_class = "codex-failed",
        source_ref = {
          kind = "external",
          ref = "owner/repo#issue/135",
        },
        dedup_key = "github-devloop/issue/owner/repo/135/2026-06-10T07-43-16Z",
        attempt = 3,
        error = "codex timed out\nwhile running decide",
      },
    })

    t.eq(#logs, 1)
    t.eq(
      logs[1],
      "consensus dept=dead_letter tag=DEAD_LETTER"
        .. " error_class=codex-failed"
        .. " fingerprint=" .. core.error_fingerprint("codex-failed", "consensus.proposal", "consensus.decide", "codex timed out\nwhile running decide")
        .. " source_ref=external:owner/repo#issue/135"
        .. " attempt=3"
        .. " terminal=true"
        .. " delivery_id=delivery/v1/raised/queue/consensus.proposal/dept/consensus.decide/01HY"
        .. " queue=consensus.proposal"
        .. " dead_dept=consensus.decide"
        .. " source_ref=external:owner/repo#issue/135"
        .. " dedup_key=github-devloop/issue/owner/repo/135/2026-06-10T07-43-16Z"
        .. " attempt=3"
        .. " error=codex timed out while running decide"
    )
  end,

  test_error_fingerprint_ignores_volatile_sha_timestamp_and_path = function()
    local first = core.error_fingerprint(
      "codex-failed",
      "consensus.proposal",
      "consensus.decide",
      "failed at 2026-06-10T01:02:03Z sha abcdef1234567890 path /tmp/fkst-a/file"
    )
    local second = core.error_fingerprint(
      "codex-failed",
      "consensus.proposal",
      "consensus.decide",
      "failed at 2027-07-11T09:08:07Z sha fedcba0987654321 path /tmp/fkst-b/file"
    )

    t.eq(first, second)
  end,

  test_wrapped_pipeline_failure_logs_delivery_error_fact_and_rethrows = function()
    local logs = {}
    local old_log = log
    log = {
      error = function(message)
        table.insert(logs, tostring(message))
      end,
    }

    local wrapped = core.wrap_pipeline_failure("decide", function(_event)
      error("consensus: codex-failed: bad sha abcdef1234567890 at 2026-06-10T01:02:03Z /tmp/fkst-a")
    end)
    local ok, err = pcall(function()
      wrapped({
        queue = "proposal",
        attempt = 6,
        terminal = false,
        payload = {
          source_ref = { kind = "external", ref = "owner/repo#issue/42" },
        },
      })
    end)

    log = old_log
    t.eq(ok, false)
    t.is_true(tostring(err):find("codex-failed", 1, true) ~= nil)
    t.eq(#logs, 1)
    t.is_true(logs[1]:find("consensus dept=decide tag=FAILURE", 1, true) ~= nil)
    t.is_true(logs[1]:find("error_class=codex-failed", 1, true) ~= nil)
    t.is_true(logs[1]:find("fingerprint=", 1, true) ~= nil)
    t.is_true(logs[1]:find("source_ref=external:owner/repo#issue/42", 1, true) ~= nil)
    t.is_true(logs[1]:find("attempt=6", 1, true) ~= nil)
    t.is_nil(logs[1]:find("terminal=", 1, true))
    t.is_true(logs[1]:find("queue=proposal", 1, true) ~= nil)
  end,
}
