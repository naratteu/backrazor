import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, writeFile, rm, realpath, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export async function verifyContracts(root) {
  const directory = await mkdtemp(join(await realpath(tmpdir()), 'backrazor-contracts-'));
  const xml = value => value.replaceAll('&','&amp;').replaceAll('"','&quot;');
  const run = (args,cwd=directory) => spawnSync(join(root,'.dotnet/dotnet'),args,{cwd,encoding:'utf8'});
  const feed=join(root,'artifacts/packages');
  try {
    const packed=run(['pack',join(root,'src/BackRazor/BackRazor.csproj'),'-c','Release','-o',feed,'--nologo'],root);
    assert.equal(packed.status,0,packed.stdout+packed.stderr);
    await writeFile(join(directory,'NuGet.Config'),`<configuration><packageSources><clear/><add key="local" value="${xml(feed)}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>`);
    await writeFile(join(directory,'Consumer.csproj'),`<Project Sdk="Microsoft.NET.Sdk.Razor"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><RestorePackagesPath>${xml(join(directory,'cache'))}</RestorePackagesPath></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/><PackageReference Include="BackRazor" Version="0.3.0-preview.1"/></ItemGroup></Project>`);
    await writeFile(join(directory,'Good.razor'),'@using BackRazor\n@attribute [BackRazor]\n<p>component</p>');
    await writeFile(join(directory,'Consumer.cs'),`using BackRazor;
public static class ConsumerApi {
 public static View Observe(Root<Consumer.Good,string> root) => root.View;
 public static Task Configure(Root<Consumer.Good,string> root) => root.ApplyAsync(["a"]);
}`);
    await writeFile(join(directory,'Display.razor'),`@using BackRazor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web.HtmlRendering
<Observer Source="Source" Read="Count" Context="size"><output>@size</output></Observer>
@code { [Parameter] public View Source { get; set; } = null!; private static int Count(HtmlRootComponent root) => root.ToHtmlString().Length; }
`);
    const good=run(['build','--nologo']);
    assert.equal(good.status,0,good.stdout+good.stderr);
    const assets=JSON.parse(await readFile(join(directory,'obj/project.assets.json'),'utf8'));
    assert.ok(!Object.keys(assets.libraries).some(name=>name.startsWith('Microsoft.CodeAnalysis')));
    await writeFile(join(directory,'Misuse.cs'),`using BackRazor;
public static class Misuse { public static void Try(View view) {
 view.Dispose(); view.Changes.OnNext(default); _ = new View();

}}`);
    await writeFile(join(directory,'RenderWrite.razor'),'@using BackRazor\n@attribute [BackRazor]\n<p>@(_count++)</p>\n@code { private int _count; }');
    const bad=run(['build','--no-restore','--nologo']);
    const diagnostics=bad.stdout+bad.stderr;
    assert.notEqual(bad.status,0,diagnostics);
    for(const member of ['Dispose','OnNext']) assert.match(diagnostics,new RegExp(`error CS1061:.*'${member}'`));
    assert.match(diagnostics,/error CS1729:.*View/);
    assert.match(diagnostics,/RenderWrite\.razor\(3,\d+\): error BRZ005:/);
    console.log('PASS: packaged runtime + transitive contracts compile without Roslyn runtime; View cannot publish, dispose, or be forged');
  } finally { await rm(directory,{recursive:true,force:true}); }
}
