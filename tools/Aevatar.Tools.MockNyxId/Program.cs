using Aevatar.Tools.MockNyxId;

var app = MockNyxIdServer.Build(args);

var options = app.Services.GetRequiredService<MockNyxIdOptions>();
var port = options.Port;
Console.WriteLine(
    $"Mock NyxID Server listening on http://localhost:{port}\n" +
    "\n" +
    "Endpoints:\n" +
    "  GET  /api/v1/users/me\n" +
    "  POST /api/v1/auth/test-token\n" +
    "  GET  /api/v1/proxy/services\n" +
    "  *    /api/v1/proxy/s/{slug}/{**path}\n" +
    "  POST /api/v1/llm/gateway/v1/chat/completions\n" +
    "\n" +
    $"Configure Aevatar: Aevatar__NyxId__Authority=http://localhost:{port}\n");

app.Run();
