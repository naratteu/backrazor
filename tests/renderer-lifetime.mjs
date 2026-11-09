import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, writeFile, rm, realpath } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const directory = await mkdtemp(join(await realpath(tmpdir()), 'backrazor-disposal-order-'));
const write = (name, source) => writeFile(join(directory, name), source);
try {
  await write('Probe.csproj', `<Project Sdk="Microsoft.NET.Sdk.Razor">
    <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework>
    <RootNamespace>Probe</RootNamespace><ImplicitUsings>enable</ImplicitUsings><NoWarn>BL0006</NoWarn></PropertyGroup>
    <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
  </Project>`);
  await write('_Imports.razor', '@using Microsoft.AspNetCore.Components\n');
  await write('Parent.razor', `@if (Visible)
{
    <Child @key="Identity" Generation="Generation" />
}
@if (Duplicate)
{
    <Child @key="Identity" Generation="Generation" />
}
@code {
    [Parameter] public int Identity { get; set; }
    [Parameter] public int Generation { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool Duplicate { get; set; }
}
`);
  await write('Child.razor', `@implements IAsyncDisposable
<p>@Generation</p>
@code {
    [Parameter] public int Generation { get; set; }
    private int _birthGeneration;
    protected override void OnInitialized()
    {
        _birthGeneration = Generation;
        Events.Record($"start-{Generation}");
    }
    protected override void OnParametersSet() => Events.Parameters.Add(Generation);
    public async ValueTask DisposeAsync()
    {
        Events.Record($"dispose-start-{_birthGeneration}");
        if (_birthGeneration == 0)
        {
            Events.DisposalStarted.TrySetResult(true);
            await Events.Release.Task;
        }
        Events.Record($"dispose-end-{_birthGeneration}");
        if (_birthGeneration == 0) Events.Finished.TrySetResult(true);
    }
}
`);
  await write('Program.cs', `using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Probe;

using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
var logs = services.GetRequiredService<ILoggerFactory>();
static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// Same key and component type: parameters change, the original instance survives.
Events.Reset();
await using (var renderer = new ProbeRenderer(services, logs))
{
    var parent = new Parent();
    await renderer.Dispatcher.InvokeAsync(async () =>
    {
        await renderer.BeginRenderingComponent(parent, ParameterView.Empty).QuiescenceTask;
        await renderer.Update(parent, ("Generation", 1));
    });
    Require(Events.Items.Count(value => value.StartsWith("start-")) == 1, "Same key recreated the child.");
    Require(Events.Parameters.Contains(1), "Updated parameters did not reach the retained child.");
    Require(!Events.Items.Any(value => value.StartsWith("dispose-")), "Same-key update disposed the child.");
    Events.Release.TrySetResult(true);
}
Console.WriteLine("PASS: same key + same component type preserves the instance when parameters change");

// A different key represents a new identity; creation need not wait for old disposal.
Events.Reset();
await using (var renderer = new ProbeRenderer(services, logs))
{
    var parent = new Parent();
    await renderer.Dispatcher.InvokeAsync(async () =>
    {
        await renderer.BeginRenderingComponent(parent, ParameterView.Empty).QuiescenceTask;
    });
    var replacement = renderer.Dispatcher.InvokeAsync(() => renderer.Update(parent, ("Identity", 1), ("Generation", 1)));
    await Events.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(Events.Items.Contains("start-1"), "Different-key creation did not overlap pending disposal.");
    Require(!Events.Items.Contains("dispose-end-0"), "The disposal gate failed.");
    Events.Release.SetResult(true);
    await replacement;
    Console.WriteLine("REPLACEMENT: " + string.Join(" -> ", Events.Items));
}

// Serialize root parameter updates and await each update's quiescence.
Events.Reset();
await using (var renderer = new ProbeRenderer(services, logs))
{
    var parent = new Parent();
    await renderer.Dispatcher.InvokeAsync(async () =>
    {
        await renderer.BeginRenderingComponent(parent, ParameterView.Empty).QuiescenceTask;
    });
    var removal = renderer.Dispatcher.InvokeAsync(() => renderer.Update(parent, ("Visible", false)));
    await Events.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(!removal.IsCompleted, "Root update did not await asynchronous child disposal.");
    async Task ReaddAfterRemoval()
    {
        await removal;
        await renderer.Dispatcher.InvokeAsync(() => renderer.Update(parent, ("Visible", true)));
    }
    var readd = ReaddAfterRemoval();
    Require(Events.Items.Count(value => value == "start-0") == 1, "Same-id readd began before removal completed.");
    Events.Release.SetResult(true);
    await readd;
    var items = Events.Items.ToArray();
    Require(Array.IndexOf(items, "dispose-end-0") < Array.LastIndexOf(items, "start-0"), "Same-id instances overlapped.");
    Console.WriteLine("SERIALIZED: " + string.Join(" -> ", items));
}
Console.WriteLine("PASS: sequential root updates await disposal before readding the same key");

// Key uniqueness applies within a sibling scope, not process-wide identity.
Events.Reset();
await using (var renderer = new ProbeRenderer(services, logs))
{
    var parent = new Parent();
    await renderer.Dispatcher.InvokeAsync(async () =>
    {
        await renderer.BeginRenderingComponent(parent, ParameterView.Empty).QuiescenceTask;
    });
    var rejected = false;
    try { await renderer.Dispatcher.InvokeAsync(() => renderer.Update(parent, ("Duplicate", true))); }
    catch (InvalidOperationException error) when (error.Message.Contains("same key")) { rejected = true; }
    finally { Events.Release.TrySetResult(true); }
    Require(rejected, "Duplicate sibling keys were accepted.");
}
Console.WriteLine("PASS: duplicate sibling keys are rejected");

public sealed class ProbeRenderer(IServiceProvider services, ILoggerFactory logs) : StaticHtmlRenderer(services, logs)
{
    public Task Update(IComponent root, params (string Name, object Value)[] values) =>
        RenderRootComponentAsync(GetComponentState(root).ComponentId,
            ParameterView.FromDictionary(values.ToDictionary(value => value.Name, value => value.Value)));
}

public static class Events
{
    public static readonly ConcurrentQueue<string> Items = new();
    public static readonly List<int> Parameters = new();
    public static TaskCompletionSource<bool> Release { get; private set; } = NewGate();
    public static TaskCompletionSource<bool> Finished { get; private set; } = NewGate();
    public static TaskCompletionSource<bool> DisposalStarted { get; private set; } = NewGate();
    private static TaskCompletionSource<bool> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static void Record(string value) => Items.Enqueue(value);
    public static void Reset()
    {
        Items.Clear(); Parameters.Clear(); Release = NewGate(); Finished = NewGate(); DisposalStarted = NewGate();
    }
}
`);
  const result = spawnSync(fileURLToPath(new URL('../.dotnet/dotnet', import.meta.url)), ['run', '--project', join(directory, 'Probe.csproj'), '--verbosity', 'quiet'], {
    cwd: directory, encoding: 'utf8', timeout: 60000,
  });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  process.stdout.write(result.stdout);
} finally {
  await rm(directory, { recursive: true, force: true });
}
