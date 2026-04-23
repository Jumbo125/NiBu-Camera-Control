using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Fotobox.WebView2Host
{
    public class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly string _baseDirectory;
        private readonly WebView2 _webView;

        public MainForm(AppConfig config, string baseDirectory)
        {
            _config = config;
            _baseDirectory = baseDirectory;

            Text = string.IsNullOrWhiteSpace(_config.Title)
                ? Program.HardcodedTitle
                : _config.Title;

            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(640, 480);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = true;

            try
            {
                var iconPath = ResolveOptionalPath(_config.Icon);
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                    this.ShowIcon = true;
                }
            }
            catch
            {
            }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            Controls.Add(_webView);

            Shown += MainForm_Shown;
        }

        private async void MainForm_Shown(object? sender, EventArgs e)
        {
            WindowState = FormWindowState.Maximized;

            if (_config.Kiosk)
            {
                SetKiosk(true);
            }

            await InitializeWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            await _webView.EnsureCoreWebView2Async();

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = _config.AllowDevTools;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = true;

            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.hostApp = window.hostApp || {
                    minimize: function () {
                        window.chrome?.webview?.postMessage({ action: 'minimize' });
                    },
                    maximize: function () {
                        window.chrome?.webview?.postMessage({ action: 'maximize' });
                    },
                    restore: function () {
                        window.chrome?.webview?.postMessage({ action: 'restore' });
                    },
                    setKiosk: function (value) {
                        window.chrome?.webview?.postMessage({ action: 'setkiosk', value: !!value });
                    },
                    toggleKioskMode: function () {
                        window.chrome?.webview?.postMessage({ action: 'togglekiosk' });
                    },
                    close: function () {
                        window.chrome?.webview?.postMessage({ action: 'close' });
                    },
                    exit: function () {
                        window.chrome?.webview?.postMessage({ action: 'exit' });
                    }
                };
            ");

            var target = ResolveStartupTarget();
            _webView.CoreWebView2.Navigate(target);
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("action", out var actionProp))
                    return;

                var action = actionProp.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(action))
                    return;

                switch (action)
                {
                    case "minimize":
                        WindowState = FormWindowState.Minimized;
                        break;

                    case "maximize":
                        SetKiosk(true);
                        break;

                    case "restore":
                        SetKiosk(false);
                        WindowState = FormWindowState.Maximized;
                        Activate();
                        break;

                    case "setkiosk":
                        if (root.TryGetProperty("value", out var kioskValue) &&
                            (kioskValue.ValueKind == JsonValueKind.True || kioskValue.ValueKind == JsonValueKind.False))
                        {
                            SetKiosk(kioskValue.GetBoolean());
                        }
                        break;

                    case "togglekiosk":
                        ToggleKiosk();
                        break;

                    case "close":
                    case "exit":
                        Close();
                        break;
                }
            }
            catch
            {
            }
        }

        private void SetKiosk(bool enabled)
        {
            if (enabled)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
                TopMost = true;
            }
            else
            {
                TopMost = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Maximized;
            }
        }


        private bool IsKioskMode()
        {
            return FormBorderStyle == FormBorderStyle.None && TopMost;
        }

        private void ToggleKiosk()
        {
            SetKiosk(!IsKioskMode());
            Activate();
        }

        private string ResolveStartupTarget()
        {
            var url = (_config.Url ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (LooksLikeAbsoluteUrl(url))
                    return url;

                var localPath = Path.GetFullPath(Path.Combine(_baseDirectory, url));
                if (File.Exists(localPath))
                    return new Uri(localPath).AbsoluteUri;

                if (LooksLikeHostWithoutScheme(url))
                    return "http://" + url;
            }

            var localIndex = string.IsNullOrWhiteSpace(_config.LocalIndexPath)
                ? "wwwroot/index.html"
                : _config.LocalIndexPath.Trim();

            var localIndexPath = Path.GetFullPath(Path.Combine(_baseDirectory, localIndex));
            if (File.Exists(localIndexPath))
                return new Uri(localIndexPath).AbsoluteUri;

            var fallbackBase = string.IsNullOrWhiteSpace(_config.DefaultUrl)
                ? "http://127.0.0.1"
                : _config.DefaultUrl.Trim().TrimEnd('/');

            var port = _config.DefaultPort <= 0 ? 8080 : _config.DefaultPort;
            return $"{fallbackBase}:{port}";
        }

        private static bool LooksLikeAbsoluteUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp
                       || uri.Scheme == Uri.UriSchemeHttps
                       || uri.Scheme == Uri.UriSchemeFile);
        }

        private static bool LooksLikeHostWithoutScheme(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.Contains("://"))
                return false;

            return value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                   || char.IsDigit(value[0]);
        }

        private string? ResolveOptionalPath(string? relativeOrAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
                return null;

            if (Path.IsPathRooted(relativeOrAbsolutePath))
                return relativeOrAbsolutePath;

            return Path.GetFullPath(Path.Combine(_baseDirectory, relativeOrAbsolutePath));
        }
    }
}