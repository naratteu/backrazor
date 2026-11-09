using BackRazor;
using BackRazor.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using R3;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;

using var services = new ServiceCollection().AddLogging().AddScoped<ScopeMarker>()
    .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
Root<Owner, Spec> Create() => new(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<ILoggerFactory>(), nameof(Owner.Items), item => item.Id);
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

Probe.Reset();
var root = Create();
await root.ApplyAsync([new("a", "port-1"), new("b", "port-2")]);
var readCount = 0;
var html = await root.View.ReadAsync(render => { readCount++; return render.ToHtmlString(); });
Require(readCount == 1 && html.Length > 0, "A consumer cannot read the raw handle on its owning Dispatcher.");
var custom = await root.View.ReadAsync(render => new { Length = render.ToHtmlString().Length });
Require(custom.Length == html.Length, "Observation forces string output instead of consumer-selected data.");
var a = Probe.Instances["a"];
var b = Probe.Instances["b"];
Require(root.View.Changes is not ISubject<Unit>, "View leaks the publisher.");
using (root.View.Changes.Subscribe(_ => { })) { }
Require(Probe.Instances.Count == 2, "Observer disposal removed operational objects.");
await root.ApplyAsync([new("b", "port-2"), new("a", "port-1")]);
Require(Probe.Instances["a"] == a && Probe.Instances["b"] == b, "Equal values/reorder recreated an object.");
Console.WriteLine("PASS: unchanged configuration and reorder preserve instances; observing does not own them");

await using (var display = new StaticHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
{
    var reads = 0;
    HtmlRootComponent rendered = default;
    await display.Dispatcher.InvokeAsync(() =>
    {
        rendered = display.BeginRenderingComponent(typeof(BackRazor.Observer<int>), ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(BackRazor.Observer<int>.Source)] = root.View,
            [nameof(BackRazor.Observer<int>.Read)] = (Func<HtmlRootComponent, int>)(_ => ++reads),
            [nameof(BackRazor.Observer<int>.ChildContent)] = (RenderFragment<int>)(value => builder => builder.AddContent(0, $"count={value}")),
        }));
    });
    async Task WaitForDisplay(string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await display.Dispatcher.InvokeAsync(() => rendered.ToHtmlString()) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Require(await display.Dispatcher.InvokeAsync(() => rendered.ToHtmlString()) == expected, "Typed ChildContent did not receive the selected value.");
    }
    await WaitForDisplay("count=1");
    await root.ApplyAsync([new("b", "port-2"), new("a", "port-1")]);
    await WaitForDisplay("count=2");
}
Require(Probe.Instances["a"] == a && Probe.Instances["b"] == b, "Observer owns operational object lifetime.");
Console.WriteLine("PASS: BackRazor.Observer<int> projects a raw render handle and renders caller-supplied ChildContent on another Dispatcher");

Probe.Blocked = "a";
var transition = root.ApplyAsync([new("b", "port-2"), new("c", "port-1")]);
await Probe.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
Require(!transition.IsCompleted && !Probe.Instances.ContainsKey("c"), "Create crossed the disposal barrier.");
Require(Probe.Instances["b"] == b, "Intersection member was recreated.");
Probe.Release.SetResult(true);
await transition;
Require(Probe.Events.IndexOf("stop:a") < Probe.Events.IndexOf("start:c"), "Exclusive resource ordering was reversed.");
Console.WriteLine("PASS: intersection render waits for asynchronous cleanup before a new id acquires the same resource");

var c = Probe.Instances["c"];
Probe.Blocked = null;
await root.ApplyAsync([new("c", "port-1", 1)]);
Require(Probe.Instances["c"] != c, "Changed configuration retained the old generation.");
Require(Probe.Events.LastIndexOf("stop:c") < Probe.Events.LastIndexOf("start:c"), "Same-id replacement overlapped.");
Console.WriteLine("PASS: same-id changed configuration stops the previous generation before creating the next");

var rejected = false;
try { await root.ApplyAsync([new("x", "port-3"), new("x", "port-4")]); }
catch (ArgumentException) { rejected = true; }
Require(rejected && root.Fault is null && Probe.Instances.ContainsKey("c"), "Invalid input mutated/faulted a healthy root.");
await root.ApplyAsync([new("c", "port-1", 1)]);
await root.DisposeAsync();
await root.DisposeAsync();
Require(Probe.Instances.Count == 0 && Probe.Resources.Count == 0, "Root disposal leaked children.");
Require(Probe.Events.LastIndexOf("stop:c") < Probe.Events.IndexOf("owner-stop"), "Owner stopped before its children.");
Require(Probe.Events.IndexOf("owner-stop") < Probe.Events.IndexOf("scope-stop"), "DI scope stopped before the renderer.");
var closed = false;
try { await root.View.ReadAsync(render => render.ToHtmlString()); }
catch (ObjectDisposedException) { closed = true; }
Require(closed, "A disposed root remains readable.");
Console.WriteLine("PASS: input rejection is nondestructive; children -> root -> DI scope shutdown is ordered and idempotent");

Probe.Reset();
root = Create();
await root.ApplyAsync([new("broken", "port-1")]);
Probe.Fail = "broken";
var failed = false;
try { await root.ApplyAsync([new("new", "port-1")]); }
catch (Exception) { failed = true; }
Require(failed && root.Fault is not null && !Probe.Events.Contains("start:new"), "Cleanup failure allowed creation.");
var faulted = false;
try { await root.ApplyAsync([new("later", "port-2")]); }
catch (InvalidOperationException) { faulted = true; }
Require(faulted && !Probe.Events.Contains("start:later"), "Faulted root accepted another transition.");
await root.DisposeAsync();
Console.WriteLine("PASS: cleanup failure faults the root and prevents every later creation");

public sealed record Spec(string Id, string Resource, int Version = 0);
public sealed class ScopeMarker : IAsyncDisposable
{
    public ValueTask DisposeAsync() { Probe.Events.Add("scope-stop"); return ValueTask.CompletedTask; }
}
public static class Probe
{
    public static readonly List<string> Events = [];
    public static readonly Dictionary<string, Guid> Instances = [];
    public static readonly HashSet<string> Resources = [];
    public static string? Blocked;
    public static string? Fail;
    public static TaskCompletionSource<bool> Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static void Reset()
    {
        Events.Clear(); Instances.Clear(); Resources.Clear(); Blocked = null; Fail = null;
        Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
