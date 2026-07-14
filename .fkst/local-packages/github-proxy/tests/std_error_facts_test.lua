-- std module behavior tests are hosted in github-proxy (a flat package, the
-- strictest single-root conformance gate) because the engine test runner only
-- scans <root>/tests and <root>/departments/* (no recursion into std/tests).
local facts = require("contract.error_facts")
local t = fkst.test

return {
  test_normalized_message_removes_timestamp_sha_and_tmp_path_noise = function()
    local first = facts.normalized_message("FAIL at 2026-06-11T20:57:25Z in /tmp/fkst-a/run sha 81bb199f4a3eda6d736d11100856a12230030b0e")
    local second = facts.normalized_error_message("fail at 2026-06-12T01:02:03Z in /tmp/fkst-b/run sha 7d9c0a1b2c3d4e5f678901234567890abcdef123")

    t.eq(first, "fail at <time>z in <path> sha <sha>")
    t.eq(second, first)
  end,

  test_stable_hash_uses_existing_fp_prefix_and_hash_algorithm = function()
    t.eq(facts.stable_hash("caught-failure|queue|dept|message"), "fp-1571597685")
    t.eq(facts.stable_hash("caught-failure|queue|dept|message"), facts.stable_hash("caught-failure|queue|dept|message"))
  end,

  test_source_ref_field_compacts_tables_and_strings = function()
    t.eq(facts.source_ref_field({ kind = "external", ref = "owner/repo#issue/42" }), "external:owner/repo#issue/42")
    t.eq(facts.source_ref_field("raw\nref"), "raw ref")
    t.is_nil(facts.source_ref_field(nil))
  end,

  test_error_fact_fields_include_available_delivery_context = function()
    local fields = facts.error_fact_fields(
      "gh-command-failed",
      "github_issue_comment_request",
      "github_comment",
      "github-proxy: gh issue comment failed: gh-command-failed: bad sha abcdef1234567890 at 2026-06-10T01:02:03Z /tmp/fkst-a",
      {
        source_ref = { kind = "external", ref = "owner/repo#issue/42" },
        attempt = 2,
        terminal = false,
      }
    )

    t.eq(fields[1], "error_class=gh-command-failed")
    t.eq(fields[2], "fingerprint=" .. facts.error_fingerprint(
      "gh-command-failed",
      "github_issue_comment_request",
      "github_comment",
      "github-proxy: gh issue comment failed: gh-command-failed: bad sha fedcba0987654321 at 2026-07-11T09:08:07Z /tmp/fkst-b"
    ))
    t.eq(fields[3], "source_ref=external:owner/repo#issue/42")
    t.eq(fields[4], "attempt=2")
    t.eq(fields[5], "terminal=false")
  end,

  test_error_fact_fields_omit_unavailable_delivery_context = function()
    local fields = facts.error_fact_fields("caught-failure", "github_poll_tick", "github_poll", "poll failed", {})

    t.eq(#fields, 2)
    t.eq(fields[1], "error_class=caught-failure")
    t.is_true(fields[2]:find("^fingerprint=fp%-") ~= nil)
  end,

  test_event_source_ref_prefers_event_field_and_falls_back_to_payload = function()
    local direct = { kind = "external", ref = "owner/repo#issue/42" }
    local payload = { kind = "external", ref = "owner/repo#issue/43" }

    t.eq(facts.event_source_ref({ source_ref = direct, payload = { source_ref = payload } }), direct)
    t.eq(facts.event_source_ref({ payload = { source_ref = payload } }), payload)
    t.is_nil(facts.event_source_ref({ payload = {} }))
    t.is_nil(facts.event_source_ref("not an event"))
  end,
}
