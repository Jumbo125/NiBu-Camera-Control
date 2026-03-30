// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: FrameHub.cs
// Zweck: Speichert das aktuelle LiveView-Bild und stellt neue Frames thread-sicher bereit.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - neueste JPEG-Frames puffern
// - Sequenznummern und Telemetrie führen
// - auf neue Frames warten und diese an Verbraucher ausliefern

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Photobox.CameraBridge.Core
{
    public sealed class FrameHub
    {
        private readonly object _lock = new object();
        private byte[] _latest;
        private long _seq;
        private TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        

        // Low-overhead telemetry (used by /api/status)
        private long _totalFrames;
        private long _lastFrameTick; // Stopwatch timestamp. 0 = never.

        public long TotalFrames => Interlocked.Read(ref _totalFrames);
        public long LastFrameTick => Volatile.Read(ref _lastFrameTick);
public void Update(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;

            

            Interlocked.Increment(ref _totalFrames);
            Volatile.Write(ref _lastFrameTick, Stopwatch.GetTimestamp());
lock (_lock)
            {
                _latest = jpeg;
                _seq++;
                _tcs.TrySetResult(true);
                _tcs = new TaskCompletionSource<bool>();
            }
        }

        public async Task<(long Seq, byte[] Jpeg)> WaitNextAsync(long lastSeq, CancellationToken ct)
        {
            Task wait;
            lock (_lock)
            {
                if (_seq != lastSeq && _latest != null)
                    return (_seq, _latest);
                wait = _tcs.Task;
            }

            await wait.WaitAsync(ct);

            lock (_lock)
                return (_seq, _latest);
        }

        public byte[] GetLatest()
        {
            lock (_lock) return _latest;
        }
    }
}