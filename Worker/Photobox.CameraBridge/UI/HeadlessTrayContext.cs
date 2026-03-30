using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Photobox.CameraBridge.Core;

namespace Photobox.CameraBridge.UI
{
    internal sealed class HeadlessTrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly string[] _args;
        private readonly Action _shutdownOnce;
        private readonly RingLogger _log;
        private int _exiting;

        // IMPORTANT: keep icon alive for lifetime of NotifyIcon
        private readonly Icon _trayIcon;

        public HeadlessTrayContext(string[] args, Action shutdownOnce, RingLogger log)
        {
            _args = args ?? Array.Empty<string>();
            _shutdownOnce = shutdownOnce ?? (() => { });
            _log = log;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Restart", null, (_, __) => Restart());
            menu.Items.Add("Exit", null, (_, __) => ExitApp());

            // Use the EXE's associated icon (your ApplicationIcon), fallback to default.
            _trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

            _tray = new NotifyIcon
            {
                Text = "Photobox CameraBridge (Headless)",
                Icon = _trayIcon,
                ContextMenuStrip = menu,
                Visible = true
            };
        }

        private void Restart()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe)) return;

                var pid = Process.GetCurrentProcess().Id;

                var cleaned = _args
                    .Where(a => !a.StartsWith("--waitforpid=", StringComparison.OrdinalIgnoreCase))
                    .Where(a => !a.StartsWith("--delayms=", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (!cleaned.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase)))
                    cleaned = cleaned.Concat(new[] { "--headless" }).ToArray();
                if (!cleaned.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
                    cleaned = cleaned.Concat(new[] { "--tray" }).ToArray();

                var argsJoined = string.Join(" ", cleaned.Select(QuoteIfNeeded));
                var restartArgs = $"{argsJoined} --waitforpid={pid} --delayms=5000";

                Process.Start(new ProcessStartInfo(exe, restartArgs) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log?.Error("Tray restart failed", ex);
            }
            finally
            {
                ExitApp();
            }
        }

        private void ExitApp()
        {
            if (Interlocked.Exchange(ref _exiting, 1) == 1) return;

            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch { }

            // If you want to be super-clean: dispose extracted icon (but NOT SystemIcons.Application).
            try
            {
                if (!ReferenceEquals(_trayIcon, SystemIcons.Application))
                    _trayIcon.Dispose();
            }
            catch { }

            try { _shutdownOnce(); } catch { }

            ExitThread();
        }

        private static string QuoteIfNeeded(string s) => s != null && s.Contains(" ") ? $"\"{s}\"" : s;
    }
}
