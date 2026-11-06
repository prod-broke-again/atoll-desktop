using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LiveWallpaper.App;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\LiveWallpaper_Engine_Singleton_Mutex";
    private static Mutex? _singletonMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Initialize logging FIRST before anything else
        var logFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper",
            "Logs");

        try { Directory.CreateDirectory(logFolder); } catch { }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logFolder, "app-.log"),
                rollingInterval: RollingInterval.Day,
                shared: true,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            Log.Information("================================================");
            Log.Information(" Starting LiveWallpaper Engine (Milestone 1)");
            Log.Information(" OS: {OSVersion}", Environment.OSVersion);
            Log.Information(" .NET: {DotNetVersion}", Environment.Version);
            Log.Information(" Args: {Args}", string.Join(" ", args));
            Log.Information("================================================");

            // 2. Check if running on interactive desktop
            if (!DesktopEnvironment.IsInteractiveDesktop() && !args.Contains("--interactive-desktop"))
            {
                Log.Information("Not on interactive desktop. Re-spawning on winsta0\\default...");
                var spawned = DesktopEnvironment.SpawnOnDefaultDesktop(args);
                Log.Information("SpawnOnDefaultDesktop result: {Spawned}", spawned);
                return;
            }

            // 3. Single instance check
            try
            {
                _singletonMutex = new Mutex(true, SingleInstanceMutexName, out var isOnlyInstance);
                if (!isOnlyInstance)
                {
                    Log.Warning("Another instance of LiveWallpaper is already running. Exiting.");
                    MessageBox.Show(
                        "Приложение LiveWallpaper уже запущено и работает в фоновом режиме в системном трее.",
                        "LiveWallpaper Engine",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to create single-instance mutex. Continuing without mutex.");
            }

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Fatal(e.Exception, "Unhandled ThreadException");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled AppDomain exception");

            var host = CreateHostBuilder(args).Build();
            host.Start();

            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;

            var appContext = services.GetRequiredService<WallpaperApplicationContext>();
            _ = services.GetRequiredService<TrayManager>();

            Application.Run(appContext);
            host.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "LiveWallpaper host terminated unexpectedly.");
        }
        finally
        {
            Log.Information("LiveWallpaper engine stopped cleanly.");
            Log.CloseAndFlush();
            try { _singletonMutex?.ReleaseMutex(); } catch { }
            _singletonMutex?.Dispose();
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IConfigManager, ConfigManager>();
                services.AddSingleton<DesktopManager>();
                services.AddSingleton<MouseHookService>();

                services.AddSingleton<Services.Pins.PinsIndexerService>();
                services.AddHostedService(sp => sp.GetRequiredService<Services.Pins.PinsIndexerService>());

                services.AddSingleton<Services.Pins.CollageEngineService>();
                services.AddSingleton<Services.Pins.ICollageEngineService>(sp => sp.GetRequiredService<Services.Pins.CollageEngineService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Pins.CollageEngineService>());

                services.AddHttpClient();
                services.AddHttpClient(Services.Telegram.TelegramUnreadService.HttpClientName, client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                });
                services.AddSingleton<Services.Weather.WeatherService>();
                services.AddSingleton<Services.Weather.IWeatherService>(sp => sp.GetRequiredService<Services.Weather.WeatherService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Weather.WeatherService>());

                services.AddSingleton<Services.Telemetry.TelemetryService>();
                services.AddSingleton<Services.Telemetry.ITelemetryService>(sp => sp.GetRequiredService<Services.Telemetry.TelemetryService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Telemetry.TelemetryService>());

                services.AddSingleton<Services.Pause.AutoPauseService>();
                services.AddSingleton<Services.Pause.IAutoPauseService>(sp => sp.GetRequiredService<Services.Pause.AutoPauseService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Pause.AutoPauseService>());

                services.AddSingleton<Services.Media.MediaService>();
                services.AddSingleton<Services.Media.IMediaService>(sp => sp.GetRequiredService<Services.Media.MediaService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Media.MediaService>());

                services.AddSingleton<Services.Launcher.LauncherService>();
                services.AddSingleton<Services.Launcher.ILauncherService>(sp => sp.GetRequiredService<Services.Launcher.LauncherService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Launcher.LauncherService>());

                services.AddSingleton<Services.Server.ISshStreamService, Services.Server.SshStreamService>();
                services.AddHostedService(sp => (Services.Server.SshStreamService)sp.GetRequiredService<Services.Server.ISshStreamService>());

                services.AddSingleton<Services.Server.ITimewebClient, Services.Server.TimewebClient>();
                services.AddSingleton<Services.Server.ISiteHealthService, Services.Server.SiteHealthService>();

                services.AddSingleton<Services.Server.IServerWidgetService, Services.Server.ServerWidgetService>();
                services.AddHostedService(sp => (Services.Server.ServerWidgetService)sp.GetRequiredService<Services.Server.IServerWidgetService>());

                services.AddSingleton<Services.Context.ContextService>();
                services.AddSingleton<Services.Context.IContextService>(sp => sp.GetRequiredService<Services.Context.ContextService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Context.ContextService>());

                services.AddSingleton<Services.Today.TodayService>();
                services.AddSingleton<Services.Today.ITodayService>(sp => sp.GetRequiredService<Services.Today.TodayService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Today.TodayService>());

                services.AddSingleton<Services.Telegram.TelegramUnreadService>();
                services.AddSingleton<Services.Telegram.ITelegramUnreadService>(sp => sp.GetRequiredService<Services.Telegram.TelegramUnreadService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Telegram.TelegramUnreadService>());

                // Widget plugins: contracts in LiveWallpaper.Core.Plugins, implementations in
                // LiveWallpaper.Services.Plugins. The host starts after the services the
                // plugins wrap, so the plugins never miss the first publication.
                services.AddSingleton(new Services.Plugins.PluginHostOptions());
                services.AddSingleton<Core.Plugins.IWidgetPlugin, Services.Plugins.TimewebPlugin>();
                services.AddSingleton<Core.Plugins.IWidgetPlugin, Services.Plugins.TelegramPlugin>();
                services.AddSingleton<Core.Plugins.IWidgetPlugin, Services.Plugins.TodayPlugin>();
                services.AddSingleton<Core.Plugins.IWidgetPlugin, Services.Plugins.ServerPlugin>();
                services.AddSingleton<Core.Plugins.IWidgetPlugin, Services.Plugins.SystemPlugin>();
                services.AddSingleton<Services.Plugins.PluginHostService>();
                services.AddHostedService(sp => sp.GetRequiredService<Services.Plugins.PluginHostService>());

                services.AddSingleton<Services.Atmosphere.AtmosphereService>();
                services.AddSingleton<Services.Atmosphere.IAtmosphereService>(sp => sp.GetRequiredService<Services.Atmosphere.AtmosphereService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.Atmosphere.AtmosphereService>());

                services.AddSingleton<Services.SetupPalette.SetupPaletteService>();
                services.AddSingleton<Services.SetupPalette.ISetupPaletteService>(sp => sp.GetRequiredService<Services.SetupPalette.SetupPaletteService>());
                services.AddHostedService(sp => sp.GetRequiredService<Services.SetupPalette.SetupPaletteService>());
                services.AddHostedService<TaskbarPanelService>();

                services.AddSingleton<WallpaperForm>();
                services.AddSingleton<WallpaperApplicationContext>();

                services.AddSingleton<TrayManager>(sp =>
                {
                    var config = sp.GetRequiredService<IConfigManager>();
                    var indexer = sp.GetRequiredService<Services.Pins.PinsIndexerService>();
                    var contextService = sp.GetRequiredService<Services.Context.IContextService>();
                    var logger = sp.GetRequiredService<ILogger<TrayManager>>();
                    var form = sp.GetRequiredService<WallpaperForm>();
                    var appContext = sp.GetRequiredService<WallpaperApplicationContext>();
                    var collage = sp.GetRequiredService<Services.Pins.ICollageEngineService>();
                    var pause = sp.GetRequiredService<Services.Pause.IAutoPauseService>();

                    return new TrayManager(
                        config,
                        indexer,
                        contextService,
                        logger,
                        changeWallpaperCallback: () =>
                        {
                            collage.RotateNow();
                        },
                        togglePauseCallback: () =>
                        {
                            pause.ToggleUserPause();
                        },
                        openSettingsCallback: () =>
                        {
                            var settingsLogger = sp.GetRequiredService<ILogger<SettingsForm>>();
                            var weather = sp.GetRequiredService<Services.Weather.IWeatherService>();
                            var serverService = sp.GetRequiredService<Services.Server.IServerWidgetService>();
                            var context = sp.GetRequiredService<Services.Context.IContextService>();
                            var atmosphere = sp.GetRequiredService<Services.Atmosphere.IAtmosphereService>();
                            var setupPalette = sp.GetRequiredService<Services.SetupPalette.ISetupPaletteService>();
                            var plugins = sp.GetServices<Core.Plugins.IWidgetPlugin>();
                            using var settingsForm = new WebSettingsForm(config, weather, serverService, context, atmosphere, setupPalette, plugins, settingsLogger);
                            settingsForm.ShowDialog();
                        },
                        exitCallback: () =>
                        {
                            appContext.Shutdown();
                        });
                });
            });
}
