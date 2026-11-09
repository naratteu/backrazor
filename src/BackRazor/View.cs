using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using R3;

namespace BackRazor;

// Read access and change notifications, without control over the root's lifetime.
public sealed class View
{
    private readonly IViewSource _source;
    public Observable<Unit> Changes { get; }

    internal View(IViewSource source, Observable<Unit> changes)
    {
        _source = source;
        Changes = changes.AsObservable();
    }

    /// <summary>Read the live render handle on its owning Dispatcher. Return a value usable by the consumer.</summary>
    public Task<T> ReadAsync<T>(Func<HtmlRootComponent, T> read, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        return _source.Dispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_source.Closed, this);
            return read(_source.Component);
        });
    }
}

internal interface IViewSource
{
    Dispatcher Dispatcher { get; }
    HtmlRootComponent Component { get; }
    bool Closed { get; }
}
