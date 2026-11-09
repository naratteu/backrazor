# BackRazor

Razor components own their state, work, updates and child object lifetimes.
Observers read their rendered output; browser presence does not drive operation.

```csharp
var root = new Root<Node, DeviceSpec>(scopes, logs, nameof(Node.Items), item => item.Id);
await root.ApplyAsync(configuration.Devices);
```

```razor
@attribute [BackRazor]
@foreach (var item in Items)
{
    <Device @key="item.Id" Spec="item" />
}
```

`ApplyAsync` serializes configuration transitions. It renders only items whose
identity **and value** remain equal, awaits removed components' asynchronous
disposal, then renders the desired configuration. Use immutable value-equal
items such as sealed records. Root items must be rendered with their unique key
and a fixed owning component type. Membership must change through ApplyAsync,
not arbitrary callbacks inside the component tree.

Disposal failure faults the root and blocks new transitions. In-flight cleanup
is never abandoned by cancellation. Root disposal renders an empty item list
and waits before disposing the root itself and its DI scope. A stalled
DisposeAsync stalls the transition; components must bound their network work.
Keep permanent work in an owned Task and cancel/await it in DisposeAsync.
Do not await an infinite worker from OnInitializedAsync: root rendering waits
for lifecycle quiescence. A child may rerender its own state normally, but
changing the configured owning-child membership must go through ApplyAsync.

`root.View` exposes change notifications and read access, with no disposal or
publication capability. The renderer does not stringify the tree on updates.
`await root.View.ReadAsync(handle => handle.ToHtmlString())` executes your reader
on the owning Dispatcher. You may instead call WriteHtmlTo or return your own
snapshot type. A live HtmlRootComponent must be read on its owning Dispatcher;
return data suitable for the consumer rather than retaining it for later reads.

```razor
<Observer Source="root.View" Read="ReadXml" Context="xml">
    <pre>@xml</pre>
</Observer>

@code {
    private static string ReadXml(HtmlRootComponent root)
        => XDocument.Parse(root.ToHtmlString()).ToString();
}
```

Observer<TValue> only manages subscription, Dispatcher access and its own UI
refresh. ChildContent receives the reader's TValue, which need not be string.
It neither disposes nor starts the operational root. Consumers choose all
conversion, formatting and display. Without observers there is no serialization
work. Direct users may subscribe to View.Changes and call View.ReadAsync.

The package includes opt-in `[BackRazor]` diagnostics and has no MQTT or web-host
dependency. It targets .NET 11. Native AOT for Blazor is experimental: preserve
application component metadata, use generated JSON contexts, and verify the
published application on its actual target. See the EdgeNode example and its
Native AOT acceptance tests. This is not a sandbox for arbitrary C# code.

Release: 0.3.0-preview.1. Download both BackRazor and BackRazor.Contracts packages
from https://github.com/naratteu/backrazor/releases and restore using that local
feed alongside nuget.org. Requires the .NET 11 SDK specified by the repository.

License: VibeCoded AI-Slop License v1.0 (included LICENSE).
Dependencies retain their own licenses.
