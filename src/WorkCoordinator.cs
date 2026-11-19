using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Ukti
{
    // Bounded, dedicated workers. Owns queued work and undelivered results so
    // cancelled loads cannot orphan a speech process when the UI is closing.
    sealed class WorkCoordinator : IDisposable
    {
        readonly Channel<WorkItem> _queue;
        readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        readonly Task[] _workers;
        readonly Action<Action> _post;
        readonly object _gate = new object();
        readonly HashSet<WorkItem> _pending = new HashSet<WorkItem>();
        bool _disposed;

        sealed class WorkItem
        {
            public Func<CancellationToken, object> Body;
            public Action<object, bool> Complete;
            public Action<object> Discard;
            public CancellationTokenSource Cancel;
            public object Result;
            public bool Ready, Cancelled;
        }
        public WorkCoordinator(Action<Action> post, int workerCount = 2)
        {
            if (post == null) throw new ArgumentNullException("post");
            if (workerCount < 1) throw new ArgumentOutOfRangeException("workerCount");
            _post = post;
            _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(16)
            { SingleReader = workerCount == 1, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
            _workers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
                _workers[i] = Task.Factory.StartNew(Run, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        // Synchronous worker delegate: the tracked Task covers its ENTIRE life,
        // unlike StartNew(asyncFunc), which only tracks a Task<Task> wrapper.
        void Run()
        {
            while (true)
            {
                WorkItem item;
                try { item = _queue.Reader.ReadAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                catch (ChannelClosedException) { return; }
                try
                {
                    if (!item.Cancel.IsCancellationRequested) item.Result = item.Body(item.Cancel.Token);
                }
                catch (OperationCanceledException) { item.Cancelled = true; }
                catch (Exception ex) { item.Result = ex; }
                item.Body = null; // Release captured audio/recognizers immediately.
                bool discard;
                lock (_gate)
                {
                    item.Cancelled |= item.Cancel.IsCancellationRequested;
                    item.Ready = true;
                    discard = _disposed;
                    if (discard) _pending.Remove(item);
                }
                if (discard) { Release(item, true); continue; }
                try { _post(delegate { Deliver(item); }); }
                catch (Exception ex)
                {
                    lock (_gate) { if (!_pending.Remove(item)) continue; }
                    Release(item, true);
                    AppLogger.Warning("Could not deliver background result: " + ex.Message);
                }
            }
        }
        void Deliver(WorkItem item)
        {
            bool discard;
            lock (_gate)
            {
                if (!_pending.Remove(item)) return; // Dispose already reclaimed it.
                discard = _disposed;
                item.Cancelled |= item.Cancel.IsCancellationRequested;
            }
            if (discard || item.Complete == null) { Release(item, true); return; }
            try { item.Complete(item.Result, item.Cancelled); }
            catch { Release(item, true); throw; }
            Release(item, false);
        }
        static void Release(WorkItem item, bool discard)
        {
            try { if (discard && item.Discard != null) item.Discard(item.Result); }
            catch (Exception ex) { AppLogger.Warning("Background cleanup failed: " + ex.Message); }
            finally
            {
                item.Cancel.Dispose();
                item.Body = null; item.Complete = null; item.Discard = null; item.Result = null;
            }
        }
        // discard owns resources in results that never reach the UI. A cancelled
        // completion can have a null result if its body never started.
        public CancellationTokenSource Submit(Func<CancellationToken, object> body, Action<object, bool> complete,
            Action<object> discard = null)
        {
            if (body == null) throw new ArgumentNullException("body");
            lock (_gate)
            {
                if (_disposed || _shutdown.IsCancellationRequested) throw new ObjectDisposedException("WorkCoordinator");
                WorkItem item = new WorkItem { Body = body, Complete = complete, Discard = discard,
                    Cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token) };
                _pending.Add(item);
                if (_queue.Writer.TryWrite(item)) return item.Cancel;
                _pending.Remove(item); Release(item, false);
                // Never block the UI waiting for a full queue to make space.
                throw new InvalidOperationException("The speech work queue is full. Try again in a moment.");
            }
        }
        public void CancelAll()
        {
            try { _shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex) { AppLogger.Warning("Cancellation cleanup: " + ex.GetBaseException().Message); }
        }
        public void Dispose()
        {
            List<WorkItem> ready = new List<WorkItem>();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (WorkItem item in _pending) if (item.Ready) ready.Add(item);
                foreach (WorkItem item in ready) _pending.Remove(item);
            }
            CancelAll();
            _queue.Writer.TryComplete();
            WorkItem queued;
            while (_queue.Reader.TryRead(out queued))
            {
                lock (_gate) _pending.Remove(queued);
                Release(queued, true);
            }
            foreach (WorkItem item in ready) Release(item, true);
            bool finished = false;
            try { finished = Task.WaitAll(_workers, 5000); }
            catch (AggregateException ex) { AppLogger.Warning("Worker shutdown: " + ex.GetBaseException().Message); finished = true; }
            if (finished) _shutdown.Dispose();
            else
            {
                AppLogger.Warning("A background operation is still stopping.");
                Task.WhenAll(_workers).ContinueWith(delegate(Task task) { _shutdown.Dispose(); }, TaskScheduler.Default);
            }
        }
    }
}
