import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, writeFile, rm, realpath, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export async function verifyAnalyzers(root) {
  const directory = await mkdtemp(join(await realpath(tmpdir()), 'backrazor-analyzers-'));
  const xml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  const write = (name, source) => writeFile(join(directory, name), source);
  const build = () => spawnSync(join(root, '.dotnet/dotnet'), ['build', '--nologo'], { cwd: directory, encoding: 'utf8' });
  try {
    const feed = join(root, 'artifacts/packages');
    const packed = spawnSync(join(root, '.dotnet/dotnet'), ['pack', join(root, 'BackRazor.Contracts/BackRazor.Contracts.csproj'),
      '--configuration', 'Release', '--output', feed, '--nologo'], { cwd: root, encoding: 'utf8' });
    assert.equal(packed.status, 0, packed.stdout + packed.stderr);
    await write('NuGet.Config', `<configuration><packageSources><clear />
      <add key="local" value="${xml(feed)}" />
      <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    </packageSources></configuration>`);
    await write('Fixtures.csproj', `<Project Sdk="Microsoft.NET.Sdk.Razor">
      <PropertyGroup><TargetFramework>net11.0</TargetFramework><RootNamespace>Fixtures</RootNamespace>
      <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
      <RestorePackagesPath>${xml(join(directory, 'cache'))}</RestorePackagesPath></PropertyGroup>
      <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />
      <PackageReference Include="BackRazor.Contracts" Version="0.3.0-preview.1" /></ItemGroup>
    </Project>`);
    await write('_Imports.razor', '@using BackRazor\n@using Microsoft.AspNetCore.Components\n');
    await write('Good.razor', `@attribute [BackRazor]
<p>@Format(Count)</p><p>@_created.ToString("O")</p>
@code {
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private int _count;
    private int Count => _count;
    private static int Format(int value) { value++; return value; }
    public void Increment() => _count++;
}
`);
    await write('Resource.razor', `@attribute [BackRazor]
@implements IDisposable
<p>owned resource</p>
@code { private IDisposable? _resource = null; public void Dispose() => _resource?.Dispose(); }
`);
    // Ordinary browser components must remain outside the opt-in policy.
    await write('Ordinary.razor', `@inject NavigationManager Navigation
<p>@(_count++)</p>
@code { private int _count; }
`);
    const good = build();
    assert.equal(good.status, 0, good.stdout + good.stderr);
    const assets = JSON.parse(await readFile(join(directory, 'obj/project.assets.json'), 'utf8'));
    assert.ok(!Object.keys(assets.libraries).some(name => name.startsWith('Microsoft.CodeAnalysis')));
    console.log('PASS: one NuGet reference supplies attribute + analyzer without a Roslyn runtime dependency');
    console.log('PASS: actual Razor generation accepts pure helpers, commands, owned-resource contracts, and unmarked components');

    const failures = [
      ['WrongBase.cs', 'BRZ001', 'using BackRazor; [BackRazor] public class WrongBase {}'],
      ['MissingDisposal.razor', 'BRZ002', '@attribute [BackRazor]\n<p>resource</p>\n@code { private IDisposable? _clock = null; }'],
      ['Browser.razor', 'BRZ003', '@attribute [BackRazor]\n@inject Microsoft.JSInterop.IJSRuntime JS\n<p>browser</p>'],
      ['AfterRender.razor', 'BRZ004', '@attribute [BackRazor]\n<p>after</p>\n@code { protected override void OnAfterRender(bool firstRender) {} }'],
      ['RenderWrite.razor', 'BRZ005', '@attribute [BackRazor]\n<p>@(_count++)</p>\n@code { private int _count; }'],
      ['HelperWrite.razor', 'BRZ005', '@attribute [BackRazor]\n<p>@Read()</p>\n@code { private int _count; private int Read() => ++_count; }'],
      ['GetterWrite.razor', 'BRZ005', '@attribute [BackRazor]\n<p>@Value</p>\n@code { private int _count; private int Value => ++_count; }'],
      ['RenderIo.razor', 'BRZ006', '@attribute [BackRazor]\n<p>@System.IO.File.ReadAllText("test.txt")</p>'],
      ['RenderClock.razor', 'BRZ006', '@attribute [BackRazor]\n<p>@DateTime.UtcNow</p>'],
      ['RenderDelegate.razor', 'BRZ006', '@attribute [BackRazor]\n<p>@_read()</p>\n@code { private readonly Func<int> _read = () => 1; }'],
      ['StaticState.razor', 'BRZ007', '@attribute [BackRazor]\n<p>static</p>\n@code { private static readonly object _shared = new(); }'],
      ['Alias.razor.cs', 'BRZ003', 'using Microsoft.AspNetCore.Components; using Nav = Microsoft.AspNetCore.Components.NavigationManager; namespace Fixtures; public partial class Alias { [Inject] public Nav Navigation { get; set; } = null!; }'],
    ];
    await write('Alias.razor', '@attribute [BackRazor]\n<p>partial class</p>');
    for (const [name, , source] of failures) await write(name, source);
    const bad = build();
    const diagnostics = bad.stdout + bad.stderr;
    assert.notEqual(bad.status, 0, 'Invalid live components unexpectedly compiled');
    for (const [name, id] of failures) {
      const escaped = name.replaceAll('.', '\\.');
      assert.match(diagnostics, new RegExp(`${escaped}\\(\\d+,\\d+\\): error ${id}:`), diagnostics);
    }
    assert.match(diagnostics, /RenderWrite\.razor\(2,\d+\): error BRZ005:/);
    assert.ok(!diagnostics.includes('error AD0001'), diagnostics);
    console.log(`PASS: ${failures.length} invalid fixtures fail with BRZ errors at original .razor/.razor.cs locations`);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}
