using BackRazor;
using BackRazor.EdgeNode;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
var configurationPath = Path.GetFullPath(builder.Configuration["config"] ?? "node.json", builder.Environment.ContentRootPath);
var initial = BackRazor.EdgeNode.Configuration.Parse(await File.ReadAllTextAsync(configurationPath));
builder.Services.AddSingleton(initial.Broker);
builder.Services.AddSingleton(services => new Root<Node, DeviceSpec>(
    services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<ILoggerFactory>(), nameof(Node.Items), item => item.Id));
builder.Services.AddSingleton<NodeService>();
builder.Services.AddHostedService(services => services.GetRequiredService<NodeService>());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, EdgeJsonContext.Default));
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitMaxRetained = 0;
#pragma warning disable ASPNETCORE9004 // .NET 11's experimental Native AOT circuit metadata hook.
    options.JsonTypeInfoResolvers.Add(EdgeJsonContext.Default);
#pragma warning restore ASPNETCORE9004
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Headers.TryGetValue("Origin", out var origin) && origin != $"{context.Request.Scheme}://{context.Request.Host}")
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.MapStaticAssets();
app.UseAntiforgery();
app.MapGet("/healthz", (Root<Node, DeviceSpec> root, NodeService service) =>
    Results.Text(root.Fault is null && service.Error is null ? "running" : "configuration-error", statusCode: root.Fault is null && service.Error is null ? 200 : 503));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
