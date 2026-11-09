using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using R3;

namespace BackRazor;

/// <summary>A process-owned root. Only immutable configuration values are retained; Renderer owns component instances.</summary>
public sealed class Root<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent, TItem> : IAsyncDisposable
    where TComponent : IComponent
    where TItem : notnull
{
    private readonly AsyncServiceScope _scope;
    private readonly ProjectionRenderer _renderer;
    private readonly Func<TItem, string> _identity;
    private readonly SemaphoreSlim _transition = new(1);
    private readonly ReplaySubject<Unit> _changes = new(1);
    private TItem[] _applied = [];
    private bool _disposed;

    public Root(IServiceScopeFactory scopes, ILoggerFactory logs, string itemsParameter, Func<TItem, string> identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemsParameter);
        ArgumentNullException.ThrowIfNull(identity);
        if (!Attribute.IsDefined(typeof(TComponent), typeof(BackRazorAttribute)))
            throw new ArgumentException("The root component must have [BackRazor].");
        _identity = identity;
        _scope = scopes.CreateAsyncScope();
        _renderer = new ProjectionRenderer(_scope.ServiceProvider, logs, itemsParameter, () => _changes.OnNext(Unit.Default));
        View = new View(_renderer, _changes);
    }

    public View View { get; }
    public Exception? Fault { get; private set; }

    /// <summary>Equal identity and value are retained. Every other old item is disposed before any replacement is rendered.</summary>
    public async Task ApplyAsync(IEnumerable<TItem> desired, CancellationToken cancellationToken = default)
    {
        var next = desired.ToArray();
        var keyed = new Dictionary<string, TItem>(StringComparer.Ordinal);
        foreach (var item in next)
        {
            var key = _identity(item);
            if (string.IsNullOrWhiteSpace(key) || !keyed.TryAdd(key, item))
                throw new ArgumentException($"Configuration identities must be nonempty and unique: '{key}'.", nameof(desired));
        }
        await _transition.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Fault is not null) throw new InvalidOperationException("The root is faulted; further transitions are disabled.", Fault);
            var retained = _applied.Where(old => keyed.TryGetValue(_identity(old), out var value) &&
                EqualityComparer<TItem>.Default.Equals(old, value)).ToArray();
            try
            {
                // Cancellation only applies before starting: abandoning an in-flight disposal would break the barrier.
                if (retained.Length != _applied.Length)
                    await _renderer.Dispatcher.InvokeAsync(() => _renderer.ApplyAsync(retained));
                await _renderer.Dispatcher.InvokeAsync(() => _renderer.ApplyAsync(next));
                _applied = next;
            }
            catch (Exception error)
            {
                Fault = error;
                throw;
            }
        }
        finally { _transition.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Children finish while their root is alive; only then dispose the root and its services.
                if (_renderer.HasRoot)
                    await _renderer.Dispatcher.InvokeAsync(() => _renderer.ApplyAsync([]));
            }
            finally
            {
                try
                {
                    await _renderer.Dispatcher.InvokeAsync(_renderer.Close);
                    await _renderer.DisposeAsync();
                }
                finally
                {
                    _changes.Dispose();
                    await _scope.DisposeAsync();
                }
            }
        }
        finally { _transition.Release(); }
    }

    private sealed class ProjectionRenderer(IServiceProvider services, ILoggerFactory logs, string itemsParameter, Action publish)
        : StaticHtmlRenderer(services, logs), IViewSource
    {
        private HtmlRootComponent _root;
        private int? _rootId;
        public bool Closed { get; private set; }
        public HtmlRootComponent Component => _rootId is not null ? _root : throw new InvalidOperationException("The root has not rendered yet.");
        public void Close() => Closed = true;
        public bool HasRoot => _rootId is not null;

        public async Task ApplyAsync(TItem[] items)
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [itemsParameter] = items });
            if (_rootId is null)
            {
                var component = InstantiateComponent(typeof(TComponent));
                _root = BeginRenderingComponent(component, parameters);
                _rootId = GetComponentState(component).ComponentId;
                await _root.QuiescenceTask;
                publish();
            }
            else
                await RenderRootComponentAsync(_rootId.Value, parameters);
        }

        protected override Task UpdateDisplayAsync(in RenderBatch batch)
        {
            if (_rootId is not null) publish();
            return base.UpdateDisplayAsync(in batch);
        }
    }
}
