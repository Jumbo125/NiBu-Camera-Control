// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Program.Hosting.cs
// Zweck: Enthält Hosting-, Konfigurations-, Middleware- und Tray-Logik des API-Servers.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - WebApplication erstellen und konfigurieren
// - Logging, Services und Swagger einrichten
// - Middleware und Utility-Endpunkte registrieren
// - Tray-Funktionen sowie Start/Stop/Restart bereitstellen
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Photobox.Bridge.Shared;
using Serilog;

namespace Photobox.Bridge.ApiServer;

public static partial class Program
{
    private static void SetupTray()
    {
        Icon icon;
        try
        {
            icon =
                Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            icon = SystemIcons.Application;
        }

        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = "CameraBridge API-Server",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restart ApiServer", null, async (_, _) => await RestartSelfProcessAsync());
        menu.Items.Add("Restart Worker", null, async (_, _) => await RestartWorkerFromTrayAsync());
        menu.Items.Add("Exit", null, async (_, _) => await ExitProcessAsync("tray_exit", 0));
        _tray.ContextMenuStrip = menu;
    }

    private static async Task StartServerAsync()
    {
        lock (Gate)
        {
            if (_app != null)
                return;
        }

        try
        {
            var app = BuildApp(_args);
            await app.StartAsync();

            lock (Gate)
                _app = app;

            try
            {
                _tray?.ShowBalloonTip(
                    1000,
                    "CameraBridge API-Server",
                    "Server started.",
                    ToolTipIcon.Info
                );
            }
            catch { }
            try
            {
                Console.WriteLine("[ApiServer] Started.");
                try
                {
                    var s = app.Services.GetRequiredService<IOptions<BridgeSettings>>().Value;
                    var pn = s?.PipeName ?? "";

                    Console.WriteLine($"[ApiServer] PipeName=\"{pn}\" (len={pn.Length})");
                    Log.Information("PipeName=\"{PipeName}\" (len={Len})", pn, pn.Length);
                }
                catch { }
            }
            catch { }
            try
            {
                Log.Information("ApiServer started.");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                if (_uiMode)
                    MessageBox.Show(ex.ToString(), "ApiServer start failed");
                else
                    Console.Error.WriteLine(ex.ToString());
            }
            catch { }

            try
            {
                Log.Error(ex, "ApiServer start failed.");
            }
            catch { }
            lock (Gate)
                _app = null;
        }
    }

    private static async Task StopServerAsync()
    {
        WebApplication? app;
        lock (Gate)
        {
            app = _app;
            _app = null;
        }

        if (app == null)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(cts.Token);
        }
        catch { }

        try
        {
            await app.DisposeAsync();
        }
        catch { }
        try
        {
            Log.CloseAndFlush();
        }
        catch { }
    }

    private static async Task RestartSelfProcessAsync()
    {
        try
        {
            Console.WriteLine("[ApiServer] Restart requested (process) ...");
        }
        catch { }
        try
        {
            Log.Information("ApiServer restart requested (process).");
        }
        catch { }

        try
        {
            if (_tray != null)
                _tray.Text = "CameraBridge API-Server (restarting...)";
        }
        catch { }

        try
        {
            await StopServerAsync();
        }
        catch { }
        try
        {
            await Task.Delay(150);
        }
        catch { }

        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in _args)
            psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            try
            {
                if (_uiMode)
                    MessageBox.Show(ex.ToString(), "Restart failed");
                else
                    Console.Error.WriteLine(ex.ToString());
            }
            catch { }

            try
            {
                Log.Error(ex, "ApiServer restart failed.");
            }
            catch { }
            try
            {
                if (_tray != null)
                    _tray.Text = "CameraBridge API-Server";
            }
            catch { }
            return;
        }

        await ExitProcessAsync("self_restart", 0);
    }

    private static async Task RestartWorkerFromTrayAsync()
    {
        try
        {
            WebApplication? app;
            lock (Gate)
                app = _app;
            if (app == null)
                return;

            var pm = app.Services.GetRequiredService<WorkerProcessManager>();
            var res = await pm.EnsureStartedAsync("tray_restart", CancellationToken.None);

            try
            {
                _tray?.ShowBalloonTip(
                    1200,
                    "Worker restart",
                    res.ok ? "Worker start triggered." : $"Failed: {res.error}",
                    res.ok ? ToolTipIcon.Info : ToolTipIcon.Error
                );
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                if (_uiMode)
                    MessageBox.Show(ex.ToString(), "Worker restart failed");
                else
                    Console.Error.WriteLine(ex.ToString());
            }
            catch { }

            try
            {
                Log.Error(ex, "Worker restart from tray failed.");
            }
            catch { }
        }
    }

    private static WebApplication BuildApp(string[] args)
    {
        var builder = CreateBuilder(args);
        ConfigureAppConfiguration(builder, args);
        ConfigureLogging(builder);
        ConfigureServices(builder);

        var app = builder.Build();
        TryTriggerWorkerAutostart(app);

        var settings = app.Services.GetRequiredService<IOptions<BridgeSettings>>().Value;
        var startedUtc = DateTime.UtcNow;

        ConfigureBindings(app, settings);
        ConfigureMiddleware(app, settings);
        ConfigureSwagger(app);
        MapUtilityEndpoints(app);
        app.MapBridgeApiEndpoints(settings, startedUtc);

        return app;
    }

    private static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var options = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        };

        return WebApplication.CreateBuilder(options);
    }

    private static void ConfigureAppConfiguration(WebApplicationBuilder builder, string[] args)
    {
        builder.Configuration.Sources.Clear();
        builder
            .Configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("ApiServer_settings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "PB_")
            .AddCommandLine(args);
    }

    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        var logFile =
            builder.Configuration.GetSection("Logging").GetValue<string>("LogFile")
            ?? "ApiServer_log.txt";
        var logPath = Path.Combine(AppContext.BaseDirectory, logFile);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true
            )
            .CreateLogger();

        builder.Host.UseSerilog();
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<BridgeSettings>(builder.Configuration.GetSection("Bridge"));
        builder.Services.PostConfigure<BridgeSettings>(s =>
        {
            s.PipeName = NormalizePipeName(s.PipeName) ?? PipeNames.CommandPipe;
        });
        builder.Services.Configure<WorkerSettings>(builder.Configuration.GetSection("Worker"));
        builder.Services.Configure<HealthSettings>(builder.Configuration.GetSection("Health"));

        builder.Services.AddSingleton<BridgePipeClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BridgeSettings>>();
            var s = opts.Value;
            Log.Information("BridgePipeClient using PipeName={PipeName}", s.PipeName);
            return new BridgePipeClient(opts);
        });

        builder.Services.AddSingleton<StreamState>();
        builder.Services.AddSingleton<WorkerHealthState>();
        builder.Services.AddSingleton<WorkerProcessManager>();
        builder.Services.AddHostedService<WorkerHealthMonitor>();

        builder.Services.AddCors(o =>
        {
            o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
    }

    private static void TryTriggerWorkerAutostart(WebApplication app)
    {
        try
        {
            var wset = app.Services.GetRequiredService<IOptions<WorkerSettings>>().Value;
            if (wset.AutoStartOnBoot)
            {
                var pm = app.Services.GetRequiredService<WorkerProcessManager>();
                _ = pm.EnsureStartedAsync("boot", CancellationToken.None);
                Log.Information("Worker autostart on boot triggered (AutoStartOnBoot=true).");
            }
            else
            {
                Log.Information("Worker autostart on boot disabled (AutoStartOnBoot=false).");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Worker autostart on boot failed (ignored).");
        }
    }

    private static void ConfigureBindings(WebApplication app, BridgeSettings settings)
    {
        app.Urls.Clear();
        app.Urls.Add($"http://{settings.BindAddress}:{settings.Port}");
    }

    private static void ConfigureMiddleware(WebApplication app, BridgeSettings settings)
    {
        app.UseCors();

        app.UseStaticFiles(
            new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(AppContext.BaseDirectory, "wwwroot")
                ),
                RequestPath = "",
            }
        );

        app.Use(async (ctx, next) =>
        {
            if (string.IsNullOrWhiteSpace(settings.AuthKey))
            {
                await next();
                return;
            }

            if (BridgeApiEndpointMappings.IsPublic(ctx, settings.MjpegPath))
            {
                await next();
                return;
            }

            var key = BridgeApiEndpointMappings.GetKey(ctx);
            if (!string.Equals(key, settings.AuthKey, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"unauthorized\"}");
                return;
            }

            await next();
        });

        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (BridgePipeException ex)
            {
                if (ctx.Response.HasStarted)
                    throw;

                Log.Warning(
                    ex,
                    "BridgePipeException. code={Code} command={Command}",
                    ex.Code,
                    ex.Command
                );

                await BridgeApiEndpointMappings.MapBridgePipeError(ex).ExecuteAsync(ctx);
            }
            catch (BridgeException be)
            {
                if (ctx.Response.HasStarted)
                    throw;

                Log.Warning(be, "BridgeException. code={Code}", be.Code);
                await BridgeApiEndpointMappings.MapBridgeError(be).ExecuteAsync(ctx);
            }
        });
    }

    private static void ConfigureSwagger(WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Photobox Bridge API v1");
            options.RoutePrefix = "docs";
            options.DocumentTitle = "Photobox Bridge API";
            options.DisplayRequestDuration();
        });
    }

    private static void MapUtilityEndpoints(WebApplication app)
    {
        app.MapGet("/swagger", () => Results.Redirect("/docs", permanent: false));
        app.MapGet("/swagger/index.html", () => Results.Redirect("/docs/index.html", permanent: false));
        app.MapGet("/openapi/v1.json", () => Results.Redirect("/swagger/v1/swagger.json", permanent: false));
    }
}
