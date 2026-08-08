local core = require("core")
local t = fkst.test
local gh_argv = require("testkit.gh_argv_mock")
gh_argv.install(t, core)

return {
  core = core,
  t = t,
  argv_rendered = gh_argv.argv_rendered,
}
