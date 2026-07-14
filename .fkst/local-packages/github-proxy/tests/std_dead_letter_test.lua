-- std module behavior tests are hosted in github-proxy (a flat package, the
-- strictest single-root conformance gate) because the engine test runner only
-- scans <root>/tests and <root>/departments/* (no recursion into std/tests).
local dead_letter = require("workflow.dead_letter")
local t = fkst.test

return {
  test_extract_source_ref_and_dedup_key_from_plain_payload = function()
    local payload = {
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      dedup_key = "dead-letter/plain",
    }

    t.eq(dead_letter.extract_source_ref(payload), "external:owner/repo#issue/42")
    t.eq(dead_letter.extract_dedup_key(payload), "dead-letter/plain")
  end,

  test_extract_source_ref_and_dedup_key_from_wrapped_payload = function()
    local payload = {
      payload = {
        source_ref = { kind = "external", ref = "owner/repo#pull/7" },
        dedup_key = "dead-letter/wrapped",
      },
    }

    t.eq(dead_letter.extract_source_ref(payload), "external:owner/repo#pull/7")
    t.eq(dead_letter.extract_dedup_key(payload), "dead-letter/wrapped")
  end,

  test_extract_source_ref_and_dedup_key_preserve_nil_behavior = function()
    local payload = {}

    t.eq(dead_letter.extract_source_ref(payload), "")
    t.is_nil(dead_letter.extract_dedup_key(payload))
  end,
}
