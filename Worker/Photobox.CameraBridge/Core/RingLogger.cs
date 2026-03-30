// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: RingLogger.cs
// Zweck: Stellt einen einfachen speicherbasierten Ringpuffer-Logger für UI und Debugging bereit.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Logzeilen im begrenzten Speicher vorhalten
// - Ereignisse für UI und File-Sinks auslösen
// - strukturierte Fehler- und Statusmeldungen sammeln

using System;
using System.Collections.Generic;

namespace Photobox.CameraBridge.Core
{
    public sealed class RingLogger
    {
        private readonly object _lock = new object();
        private readonly Queue<string> _lines = new Queue<string>();
        private readonly int _capacity;

        public RingLogger(int capacity = 2000) => _capacity = Math.Max(100, capacity);

        public event Action<string> LineAppended;

        /// <summary>
        /// Structured event for log sinks (e.g. file logging) that need the Exception/stack trace.
        /// </summary>
        public event Action<DateTime, string, string, Exception> EntryAppended;

        public void Info(string msg) => Append("INFO", msg, null);
        public void Warn(string msg) => Append("WARN", msg, null);
        public void Warn(string msg, Exception ex) => Append("WARN", msg, ex);   // <- add
        public void Error(string msg, Exception ex) => Append("ERROR", msg, ex);

        private void Append(string level, string msg, Exception ex)
        {
            var ts = DateTime.Now;
            var line = $"{ts:HH:mm:ss} [{level}] {msg}" + (ex != null ? $" | {ex.GetType().Name}: {ex.Message}" : "");
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity) _lines.Dequeue();
            }
            EntryAppended?.Invoke(ts, level, msg, ex);
            LineAppended?.Invoke(line);
        }

        public string[] Snapshot()
        {
            lock (_lock) return _lines.ToArray();
        }
    }
}