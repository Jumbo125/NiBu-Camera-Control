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
        // Keeps the live log file at a readable size. One rotated backup (".1") is kept alongside it,
        // so max disk usage is ~2x this value. Without this cap the file grows forever (every capture
        // logs several lines, and the USB watchdog logs every ~2.5s while a camera is disconnected),
        // which is how it silently reaches double-digit MB and becomes painful to open.
        private const long MaxSizeBytes = 5 * 1024 * 1024; // 5 MB

        private readonly object _lock = new object();
        private readonly RingLogger _logger;
        private StreamWriter _writer;
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

            // If a previous run already left an oversized log behind, rotate it before appending.
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length >= MaxSizeBytes)
                    RotateFile();
            }
            catch { /* best effort */ }

            OpenWriter();

            WriteRaw($"\r\n===== START {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

            _logger.EntryAppended += OnEntry;
        }

        private void OpenWriter()
        {
            // Append mode, allow reading while writing. FileShare.Delete lets another
            // process move/replace this file (e.g. its own RotateFile()) instead of
            // failing with a sharing violation if two instances ever overlap.
            _writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                AutoFlush = true
            };
        }

        /// <summary>Moves the current log file to "&lt;name&gt;.1" (overwriting any previous backup).</summary>
        private void RotateFile()
        {
            var backupPath = LogPath + ".1";
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
            try { File.Move(LogPath, backupPath); } catch { }
        }

        private void RotateIfOversized()
        {
            long len;
            try { len = _writer.BaseStream.Length; } catch { return; }
            if (len < MaxSizeBytes) return;

            try { _writer.Dispose(); } catch { }

            RotateFile();

            try
            {
                OpenWriter();
                WriteRaw($"===== LOG ROTATED {DateTime.Now:yyyy-MM-dd HH:mm:ss} (older entries in {Path.GetFileName(LogPath)}.1) =====");
            }
            catch { /* best effort: if reopening fails, next OnEntry call will retry via its own try/catch */ }
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

                    RotateIfOversized();
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
