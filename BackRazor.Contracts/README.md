# BackRazor.Contracts 0.3.0-preview.1

An opt-in contract and analyzer for Razor components used as live objects.

```razor
@using BackRazor
@attribute [BackRazor]

<p>@_count</p>

@code {
    private int _count;
    public void Increment() { _count++; StateHasChanged(); }
}
```

The package bundles the small `BackRazor.Contracts.dll` runtime attribute and
`BackRazor.Analyzers.dll` under `analyzers/dotnet/cs`. Roslyn is not a runtime
dependency. Install this package directly in every project that defines marked
components. Targets .NET Standard 2.0; requires a Roslyn 4.13 or newer compiler.
The Razor integration is tested with the .NET 11 SDK.

| Error | Contract |
| --- | --- |
| BRZ001 | A marked class must derive from ComponentBase. |
| BRZ002 | A class with owned disposable fields must implement IDisposable or IAsyncDisposable. |
| BRZ003 | Marked code cannot reference JS interop, HTTP context, circuit, NavigationManager, ElementReference, or AuthenticationStateProvider types. |
| BRZ004 | Browser completion lifecycle methods are forbidden. |
| BRZ005 | Rendering and its source-visible helpers cannot assign state or increment fields. |
| BRZ006 | Render calls must be source-visible and inspectable, or supported value formatting/framework builder calls. |
| BRZ007 | Static fields must be const. |

Commands and initialization may change instance state. The analyzer follows
source-visible render helpers and property getters, including partial classes.
Known safe formatting includes primitive/Guid/date/time ToString methods, string
Length/IsNullOrEmpty/IsNullOrWhiteSpace, and framework render builder calls.
Other external render calls are rejected conservatively.

Diagnostics include generated Razor C# and retain its `#line` mapping so build
errors point to original `.razor` or code-behind locations. Editor squiggles
depend on the editor's Razor diagnostic integration and are not independently
verified by these build tests.

This is a prototype ruleset, not a C# purity proof or sandbox. It does not prove
that Dispose releases every resource, inspect every child component, detect
dynamic observation cycles, or prohibit deliberate analyzer suppression. Value
formatting can still depend on culture. Compiler-generated implicit calls and
runtime dispatch can require additional rules. The separate BackRazor package supplies Root<TComponent,TItem>, View and Observer<TValue>.
It references this contract package. Consumers supply the Observer reader and ChildContent to choose conversion and display.
