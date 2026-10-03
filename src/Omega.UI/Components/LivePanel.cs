using Microsoft.AspNetCore.Components;
using Omega.UI.Presentation;

namespace Omega.UI.Components;

/// <summary>
/// A view that loads its data when rendered and, once interactive, refreshes it every Dashboard:RefreshInterval
/// (ADR-016: polling the API from the UI server; the Blazor circuit pushes the change to the browser).
/// </summary>
public abstract class LivePanel : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private bool _loopStarted;

    [Inject] protected DashboardOptions Options { get; set; } = null!;

    [Inject] protected TimeProvider Time { get; set; } = null!;

    /// <summary>When the data on screen was last refreshed.</summary>
    protected DateTimeOffset RefreshedAtUtc { get; private set; }

    protected override async Task OnParametersSetAsync() => await RefreshAsync();

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive && !_loopStarted)
        {
            _loopStarted = true;
            _ = LoopAsync();
        }
    }

    protected abstract Task LoadAsync(CancellationToken cancellationToken);

    protected CancellationToken Stopping => _stopping.Token;

    private async Task RefreshAsync()
    {
        try
        {
            await LoadAsync(_stopping.Token);
            RefreshedAtUtc = Time.GetUtcNow();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(Options.RefreshInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                await RefreshAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }
}
