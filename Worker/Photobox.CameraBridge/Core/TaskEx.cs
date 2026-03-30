// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: TaskEx.cs
// Zweck: Ergänzt Hilfsmethoden für Task-Waiting mit Cancellation und Timeout.
// Projekt: Photobox CameraBridge Worker

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Photobox.CameraBridge.Core
{
    internal static class TaskEx
    {
        public static async Task WaitAsync(this Task task, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();

            using (ct.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
            {
                var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }

     public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
{
    var delayTask = Task.Delay(timeout);

    var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
    if (completed == delayTask)
        throw new TimeoutException($"Task timed out after {timeout.TotalSeconds:0.##}s.");

    // task ist fertig -> Ergebnis/Exception weitergeben
    return await task.ConfigureAwait(false);
}

    }
}
