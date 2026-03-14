using Aevatar.Tools.Config;

var noBrowser = args.Contains("--no-browser");
var port = 6677;
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var customPort))
    port = customPort;

await ConfigToolHost.RunAsync(new ConfigToolHostOptions
{
    Port = port,
    NoBrowser = noBrowser,
    BannerTitle = "aevatar-config",
    DeprecationMessage = "Deprecated: use `aevatar config ui`.",
});
