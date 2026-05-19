namespace Aevatar.Demos.Inspector;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var app = InspectorApplication.CreateApp(args);
        InspectorApplication.PrintStartupBanner(app.Services.GetRequiredService<InspectorRuntimeOptions>());
        await app.RunAsync();
    }
}
