local t = fkst.test

return {
  test_valid_round_accepts_only_integer_rounds_within_bound = function()
    local rounds = require("devloop.rounds")

    t.eq(rounds.valid_round(0), 0)
    t.eq(rounds.valid_round("42"), 42)
    t.eq(rounds.valid_round(100000), 100000)
    t.is_nil(rounds.valid_round(nil))
    t.is_nil(rounds.valid_round(-1))
    t.is_nil(rounds.valid_round(1.5))
    t.is_nil(rounds.valid_round("1.5"))
    t.is_nil(rounds.valid_round(100001))
    t.is_nil(rounds.valid_round("not-a-round"))
  end,
}
