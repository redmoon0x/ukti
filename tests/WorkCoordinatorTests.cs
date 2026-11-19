using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ukti
{
    static class WorkCoordinatorTests
    {
        sealed class Resource : IDisposable
        {
            public int Disposals;
            public void Dispose() { Interlocked.Increment(ref Disposals); }
        }
        static void Wait(Func<bool> condition)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < end) Thread.Sleep(5);
            if (!condition()) throw new Exception("Worker test timed out.");
        }
        internal static void Run(Action<bool, string> assert)
        {
            ConcurrentQueue<Action> posts = new ConcurrentQueue<Action>();
            using (ManualResetEventSlim started = new ManualResetEventSlim())
            using (ManualResetEventSlim release = new ManualResetEventSlim())
            using (WorkCoordinator work = new WorkCoordinator(posts.Enqueue, 1))
            {
                bool cancelled = false;
                object value = null;
                CancellationTokenSource token = work.Submit(delegate(CancellationToken cancel)
                { started.Set(); release.Wait(); return "payload"; }, delegate(object result, bool wasCancelled)
                { value = result; cancelled = wasCancelled; });
                assert(started.Wait(5000), "Worker must start.");
                Task[] tasks = (Task[])typeof(WorkCoordinator).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(work);
                assert(!tasks[0].IsCompleted && !(tasks[0] is Task<Task>), "Tracked task must cover the real worker lifetime.");
                token.Cancel(); release.Set();
                Wait(delegate { return posts.Count == 1; });
                Action deliver; posts.TryDequeue(out deliver); deliver();
                assert(cancelled && (string)value == "payload", "Cancellation during execution must reach completion without losing its cleanup payload.");
            }
            using (ManualResetEventSlim started = new ManualResetEventSlim())
            using (ManualResetEventSlim release = new ManualResetEventSlim())
            using (WorkCoordinator work = new WorkCoordinator(posts.Enqueue, 1))
            {
                bool ran = false, cancelled = false;
                work.Submit(delegate { started.Set(); release.Wait(); return null; }, null);
                assert(started.Wait(5000), "Blocking operation must start.");
                CancellationTokenSource queued = work.Submit(delegate { ran = true; return "unexpected"; },
                    delegate(object result, bool wasCancelled) { cancelled = wasCancelled && result == null; });
                queued.Cancel(); release.Set();
                Wait(delegate { return posts.Count == 2; });
                Action deliver; while (posts.TryDequeue(out deliver)) deliver();
                assert(!ran && cancelled, "Pre-cancelled work must skip its body and deliver a safe null cancellation result.");
            }
            Resource resource = new Resource();
            bool called = false;
            WorkCoordinator pending = new WorkCoordinator(posts.Enqueue);
            pending.Submit(delegate { return resource; }, delegate { called = true; }, delegate(object result) { ((Resource)result).Dispose(); });
            Wait(delegate { return posts.Count == 1; });
            pending.Dispose();
            Action late; posts.TryDequeue(out late); late();
            assert(resource.Disposals == 1 && !called, "Shutdown must reclaim an undelivered native result exactly once, even if its UI callback arrives later.");
            using (ManualResetEventSlim started = new ManualResetEventSlim())
            {
                bool finished = false;
                WorkCoordinator work = new WorkCoordinator(posts.Enqueue, 1);
                work.Submit(delegate { started.Set(); Thread.Sleep(180); finished = true; return null; }, null);
                assert(started.Wait(5000), "Shutdown test operation must start.");
                Stopwatch wait = Stopwatch.StartNew(); work.Dispose();
                assert(finished && wait.ElapsedMilliseconds >= 100, "Dispose must join the actual running worker, not an already-completed async wrapper.");
            }
            using (ManualResetEventSlim started = new ManualResetEventSlim())
            {
                bool cancellationObserved = false;
                WorkCoordinator work = new WorkCoordinator(posts.Enqueue, 1);
                work.Submit(delegate(CancellationToken cancel)
                {
                    started.Set(); cancellationObserved = cancel.WaitHandle.WaitOne(3000);
                    cancel.ThrowIfCancellationRequested(); return null;
                }, null);
                assert(started.Wait(5000), "Cancellable shutdown operation must start.");
                for (int i = 0; i < 16; i++) work.Submit(delegate { return null; }, null);
                bool full = false;
                try { work.Submit(delegate { return null; }, null); } catch (InvalidOperationException) { full = true; }
                assert(full, "A full queue must fail promptly, not freeze the UI.");
                work.Dispose();
                assert(cancellationObserved, "Shutdown cancellation must reach the token supplied to active work.");
            }
            float[] samples = new float[20000];
            for (int i = 0; i < samples.Length; i++) samples[i] = i / 20000f;
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                AsrProcess.TransferSamples(writer, samples); writer.Flush(); stream.Position = 0;
                float[] received = AsrProcess.ReceiveSamples(new BinaryReader(stream), samples.Length);
                assert(received.Length == samples.Length && received[12345] == samples[12345] && received[19999] == samples[19999],
                    "Chunked worker audio protocol must retain all samples across buffer boundaries.");
                bool rejected = false;
                try { AsrProcess.ReceiveSamples(new BinaryReader(stream), -1); } catch (InvalidDataException) { rejected = true; }
                assert(rejected, "Worker must reject invalid packet lengths before allocating audio memory.");
            }
        }
    }
}
