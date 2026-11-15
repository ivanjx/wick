using Microsoft.AspNetCore.Components;

namespace Wick.Benchmarks.Rendering;

/// <summary>
/// Runs every work item immediately on the calling thread, serialized by a lock like Blazor's own dispatcher, so a
/// render triggered from any thread completes before <c>InvokeAsync</c> returns. After the outermost work item it
/// calls <c>onIdle</c> while still holding the lock: at that point nothing more is running on the dispatcher.
/// </summary>
/// <remarks>Only the synchronous part of an asynchronous work item is covered; the benchmark components have none.</remarks>
internal sealed class InlineDispatcher(Action onIdle) : Dispatcher
{
    private readonly Lock _gate = new();
    private int _depth;

    public override bool CheckAccess() => _gate.IsHeldByCurrentThread;

    public override Task InvokeAsync(Action workItem)
    {
        lock (_gate)
        {
            _depth++;

            try
            {
                workItem();

                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
            finally
            {
                Exit();
            }
        }
    }

    public override Task InvokeAsync(Func<Task> workItem)
    {
        lock (_gate)
        {
            _depth++;

            try
            {
                return workItem();
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
            finally
            {
                Exit();
            }
        }
    }

    public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem)
    {
        lock (_gate)
        {
            _depth++;

            try
            {
                return Task.FromResult(workItem());
            }
            catch (Exception exception)
            {
                return Task.FromException<TResult>(exception);
            }
            finally
            {
                Exit();
            }
        }
    }

    public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem)
    {
        lock (_gate)
        {
            _depth++;

            try
            {
                return workItem();
            }
            catch (Exception exception)
            {
                return Task.FromException<TResult>(exception);
            }
            finally
            {
                Exit();
            }
        }
    }

    private void Exit()
    {
        if (--_depth == 0)
        {
            onIdle();
        }
    }
}
