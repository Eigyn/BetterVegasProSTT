using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Subtitler.VegasPlugin
{
    public static class UiThread
    {
        // VEGAS does not guarantee a WinForms SynchronizationContext in its
        // extension callbacks. Explicitly dispatch every post-worker host call.
        public static Task RunAsync(Control owner, Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler closed = null;
            closed = (sender, args) => { owner.Disposed -= closed; completion.TrySetResult(false); };
            Action execute = () => {
                owner.Disposed -= closed;
                if (owner.IsDisposed || owner.Disposing) { completion.TrySetResult(false); return; }
                try { action(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
            };
            owner.Disposed += closed;
            if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) {
                owner.Disposed -= closed; completion.TrySetResult(false);
            } else if (!owner.InvokeRequired) execute();
            else {
                try { owner.BeginInvoke(execute); }
                catch (InvalidOperationException) {
                    owner.Disposed -= closed;
                    if (owner.IsDisposed || !owner.IsHandleCreated) completion.TrySetResult(false);
                    else completion.TrySetException(new InvalidOperationException("The Subtitler window could not receive the completed recognition job."));
                }
            }
            return completion.Task;
        }
    }
}
