// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: MtaWorker.cs
// Zweck: Führt SDK-nahe Kameraoperationen auf einem dedizierten Worker-Thread mit Message-Loop aus.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Aktionen thread-sicher an den Kamera-Thread übergeben
// - Message-Pump für SDK-Callbacks bereitstellen
// - synchrone und asynchrone Aufrufe serialisieren

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Photobox.CameraBridge.Core
{
    public sealed class MtaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private readonly RingLogger _logger;

        private volatile int _threadId;
        private volatile bool _running = true;

        private ApplicationContext _ctx;
        private System.Windows.Forms.Timer _timer;
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);

        public MtaWorker(RingLogger logger)
        {
            _logger = logger;

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "Camera-SDK-Worker"
            };

            // Canon EDSDK: callbacks brauchen Message Loop -> STA + Pump
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            _started.Wait();
        }

        public bool IsOnWorkerThread => Thread.CurrentThread.ManagedThreadId == _threadId && _threadId != 0;

        public Task InvokeAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsOnWorkerThread)
            {
                try { action(); return Task.CompletedTask; }
                catch (Exception ex) { return Task.FromException(ex); }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            if (IsOnWorkerThread)
            {
                try { return Task.FromResult(func()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        private void ThreadMain()
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;

            _ctx = new ApplicationContext();
            _timer = new System.Windows.Forms.Timer { Interval = 5 };
            _timer.Tick += (_, __) => DrainQueue();
            _timer.Start();

            _started.Set();
            Application.Run(_ctx);

            try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        }

        private void DrainQueue()
        {
            // Nicht ewig blockieren, damit Messages weiter gepumpt werden
            int processed = 0;
            while (processed < 200 && _queue.TryTake(out var action))
            {
                processed++;
                try { action?.Invoke(); }
                catch (Exception ex) { _logger.Error("SDK action failed", ex); }
            }

            if (!_running && _queue.Count == 0)
            {
                try { _ctx?.ExitThread(); } catch { }
            }
        }

        public void Dispose()
        {
            _running = false;

            try { _queue.Add(() => { try { _ctx?.ExitThread(); } catch { } }); } catch { }
            try { _queue.CompleteAdding(); } catch { }

            try { _thread.Join(1500); } catch { }
        }
    }
}
