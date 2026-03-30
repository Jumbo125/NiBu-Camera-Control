// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: FileLogSink.cs
// Zweck: Schreibt Logeinträge des RingLoggers dauerhaft in eine Datei.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Logzeilen inklusive Fehlerdetails in eine Datei anhängen
// - gleichzeitiges Lesen der Logdatei erlauben
// - Dateilogging für Headless- und Debug-Betrieb bereitstellen

using System;
using System.IO;

namespace Photobox.CameraBridge.Core
{
    /// <summary>
    /// Simple file sink that subscribes to RingLogger and appends log lines (incl. stack traces) to a file.
    /// Designed for headless debugging.
    /// </summary>
    public sealed class FileLogSink : IDisposable
    {
        private readonly object _lock = new object();
        private readonly RingLogger _logger;
        private readonly StreamWriter _writer;
        private bool _disposed;

        public string LogPath { get; }

        public FileLogSink(RingLogger logger, string logPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(logPath)) throw new ArgumentNullException(nameof(logPath));

            LogPath = logPath;

            // Ensure directory exists (for absolute paths). For base-dir logging this is typically already there.
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { /* best effort */ }

            // Append mode, allow reading while writing.
            _writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

            WriteRaw($"\r\n===== START {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

            _logger.EntryAppended += OnEntry;
        }

        private void OnEntry(DateTime ts, string level, string msg, Exception ex)
        {
            if (_disposed) return;

            try
            {
                lock (_lock)
                {
                    _writer.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");

                    if (ex != null)
                    {
                        // Full ToString() includes stack trace.
                        _writer.WriteLine(ex.ToString());
                    }
                }
            }
            catch
            {
                // Never crash the app because file logging failed.
                // If writing fails repeatedly (disk full etc.), we silently ignore.
            }
        }

        private void WriteRaw(string line)
        {
            try
            {
                lock (_lock)
                {
                    _writer.WriteLine(line);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _logger.EntryAppended -= OnEntry; } catch { }

            WriteRaw($"===== END {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

            try { _writer?.Dispose(); } catch { }
        }
    }
}
