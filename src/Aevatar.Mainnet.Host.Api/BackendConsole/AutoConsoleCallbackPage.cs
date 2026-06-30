namespace Aevatar.Mainnet.Host.Api.BackendConsole;

// Single shared OIDC PKCE redirect target for the unified "Aevatar Backend Console" suite
// (observatory / studio / schedules / channels / voice). Each console page initiates login with
// redirect_uri = <origin>/auto/callback and stores a PKCE blob (verifier + state + returnTo) in
// sessionStorage under the shared key aevatar-console:nyxid:pkce:pkce. This minimal page completes
// the authorization-code exchange against nyxid (same authority/clientId as every sibling page),
// writes the resulting token under the shared key aevatar-console:nyxid:pkce:token, and bounces back
// to returnTo. Because all five pages read that one shared token key, a single login spans the suite.
internal static class AutoConsoleCallbackPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>登录中 · Aevatar Backend Console</title>
<style>
  :root { color-scheme: dark; }
  html,body { height:100%; margin:0; }
  body { background:#0b0d12; color:#e6e8ee; display:grid; place-items:center;
    font:14px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,"Helvetica Neue",Arial,"PingFang SC","Microsoft YaHei",sans-serif; }
  .card { text-align:center; padding:32px 28px; max-width:420px; }
  .spinner { width:30px; height:30px; margin:0 auto 18px; border:3px solid rgba(255,255,255,.15);
    border-top-color:#6ea8fe; border-radius:50%; animation:spin .8s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }
  h1 { font-size:15px; font-weight:600; margin:0 0 6px; letter-spacing:-.01em; }
  p { margin:0; color:#9aa3b2; font-size:13px; }
  a { color:#6ea8fe; text-decoration:none; }
  a:hover { text-decoration:underline; }
  .back { margin-top:16px; display:none; }
</style>
</head>
<body>
  <main class="card" role="status" aria-live="polite">
    <div class="spinner" id="spin" aria-hidden="true"></div>
    <h1>Aevatar Backend Console</h1>
    <p id="msg">正在完成登录…</p>
    <p class="back" id="back"></p>
  </main>
<script>
(function(){
  "use strict";
  var AUTHORITY = "https://nyx.chrono-ai.fun";
  var CLIENT_ID = "37a93189-2734-406e-bca1-7dbdf25c5a53";
  var REDIRECT_URI = location.origin + "/auto/callback";
  var STORAGE_KEY = "aevatar-console:nyxid:pkce";
  var TOKEN_KEY = STORAGE_KEY + ":token";
  var PKCE_KEY  = STORAGE_KEY + ":pkce";
  var DEFAULT_RETURN = "/workflow/observatory";

  function safePath(p){ return (typeof p === "string" && p.charAt(0) === "/" && p.charAt(1) !== "/") ? p : DEFAULT_RETURN; }
  function stop(){ var s=document.getElementById("spin"); if(s) s.style.display="none"; }
  function show(t){ var m=document.getElementById("msg"); if(m) m.textContent=t; }
  function offerBack(to){ stop(); var b=document.getElementById("back"); if(b){ b.innerHTML='<a href="'+to+'">返回控制台</a>'; b.style.display="block"; } }

  var params = new URLSearchParams(location.search);
  var code = params.get("code");
  var oauthError = params.get("error");
  var pending = null;
  try { pending = JSON.parse(sessionStorage.getItem(PKCE_KEY) || "null"); } catch(e){ pending = null; }
  var returnTo = safePath(pending && pending.returnTo);

  if(oauthError){ show("登录失败：" + oauthError); offerBack(returnTo); return; }
  if(!code){ location.replace(returnTo); return; }
  if(!pending || pending.state !== params.get("state")){ show("登录状态校验失败，请返回重试。"); offerBack(returnTo); return; }

  var form = new URLSearchParams();
  form.set("grant_type", "authorization_code");
  form.set("code", code);
  form.set("redirect_uri", REDIRECT_URI);
  form.set("client_id", CLIENT_ID);
  form.set("code_verifier", pending.verifier);

  fetch(AUTHORITY + "/oauth/token", { method:"POST", headers:{ "Content-Type":"application/x-www-form-urlencoded" }, body: form.toString() })
    .then(function(res){
      try { sessionStorage.removeItem(PKCE_KEY); } catch(e){}
      if(!res.ok){ show("令牌交换失败（" + res.status + "），请返回重试。"); offerBack(returnTo); return null; }
      return res.json();
    })
    .then(function(token){
      if(!token) return;
      token.obtained_at = Date.now();
      localStorage.setItem(TOKEN_KEY, JSON.stringify(token));
      location.replace(returnTo);
    })
    .catch(function(e){ show("登录出错：" + (e && e.message ? e.message : e)); offerBack(returnTo); });
})();
</script>
</body>
</html>
""";
}
