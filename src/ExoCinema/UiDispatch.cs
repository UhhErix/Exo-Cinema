using Microsoft.UI.Dispatching;

namespace ExoCinema;

/// <summary>
/// The only place the app posts to the UI thread: <c>DispatcherQueue.Post</c> runs the action through <see cref="UiGuard"/>.
/// A raw <c>TryEnqueue</c> anywhere else is refused by the source-guard test (UiGuardTests).
/// </summary>
internal static class UiDispatch
{
    internal static bool Post(this DispatcherQueue queue, Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) =>
        queue.TryEnqueue(priority, () => UiGuard.Run(action, "dispatcher"));
}
