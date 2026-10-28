using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MarkMello.Application;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure;
using MarkMello.Infrastructure.Diagnostics;
using MarkMello.Infrastructure.Platform;
using MarkMello.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Desktop;

internal static class Program
{
    /// <summary>
    /// Точка входа. Порядок шагов важен:
    /// 1) создаём <see cref="StopwatchStartupMetrics"/> первым делом, чтобы засечь весь startup;
    /// 2) собираем DI контейнер с args для command-line activation;
    /// 3) передаём провайдер в <see cref="App"/> до запуска Avalonia builder'а;
    /// 4) стартуем classic desktop lifetime.
    /// Никаких editor-зависимостей в этом графе не должно быть (constitution §4).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var metrics = new StopwatchStartupMetrics();
        metrics.Mark(StartupStage.AppBootstrap);

        // Файл из проводника уходит в уже работающее окно новой вкладкой; новый процесс не нужен.
        using var channel = ClaimSingleInstance(args, out var forwarded);
        if (forwarded)
        {
            return 0;
        }

        using var services = ConfigureServices(metrics, args);
        App.RegisterServices(services);
        channel?.StartListening(path => OnPathReceived(services, path));

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Первый экземпляр получает канал. Если экземпляр уже есть и в аргументах файл,
    /// путь отправляется ему (<paramref name="forwarded"/> = true). Запуск без файла
    /// и диагностический smoke-режим остаются самостоятельными, как раньше.
    /// </summary>
    private static SingleInstanceChannel? ClaimSingleInstance(string[] args, out bool forwarded)
    {
        forwarded = false;
        if (Environment.GetEnvironmentVariable("MARKMELLO_SMOKE_EXIT_AFTER_OPEN") is not null
            || Environment.GetEnvironmentVariable("MARKMELLO_SMOKE_EXIT_AFTER_OPEN_MS") is not null)
        {
            return null;
        }

        var channel = SingleInstanceChannel.TryClaim();
        if (channel is not null)
        {
            return channel;
        }

        var path = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a) && SupportedDocumentTypes.IsSupportedPath(a));
        if (path is null)
        {
            return null;
        }

        // Разрешаем уже работающему окну выйти на передний план: иначе Windows лишь мигнёт значком.
        AllowForeground();
        forwarded = SingleInstanceChannel.TrySend(Path.GetFullPath(path));
        return null;
    }

    private static void OnPathReceived(IServiceProvider services, string path)
        => Dispatcher.UIThread.Post(() =>
        {
            services.GetRequiredService<IFileActivationPublisher>().NotifyFileActivated(path);

            if (global::Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            }
        });

    private static void AllowForeground()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AllowSetForegroundWindow(-1);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Используется Avalonia design-time previewer'ом — должно быть public и static.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static ServiceProvider ConfigureServices(IStartupMetrics metrics, string[] args)
    {
        var collection = new ServiceCollection();
        collection.AddInfrastructure(metrics, args);
        collection.AddApplication();
        collection.AddPresentation();
        return collection.BuildServiceProvider();
    }
}
