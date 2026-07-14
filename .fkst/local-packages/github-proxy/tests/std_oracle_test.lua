local oracle = require("workflow.oracle")

return {
  test_effect_set_unions_writes_and_raises = function()
    local rec = oracle.recorder()
    rec.record_write({ op = "post_comment", target = "owner/repo#issue/42" })
    rec.record_raise({ queue = "github-proxy.comment_request", dedup_key = "k1" })
    local effects = rec.effects()
    assert(#effects == 2)
    local key = oracle.effect_key(effects[1])
    assert(type(key) == "string" and #key > 0, "every effect must have a stable string key")
  end,

  test_delivery_equivalence_ignores_reads = function()
    local rec = oracle.recorder()
    rec.record_read({ op = "read_issue" })
    assert(#rec.effects() == 0)
  end,
}
