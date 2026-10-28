using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace MarkMello.Infrastructure.Platform;

/// <summary>
/// Один процесс на пользователя и на exe: второй запуск с файлом передаёт путь
/// уже работающему экземпляру и сразу завершается, а тот открывает файл новой вкладкой.
///
/// Владение определяется именованным мьютексом, путь передаётся по именованному каналу.
/// Имена зависят от пути к exe: установленная и портативная сборки не мешают друг другу.
/// Ничего из этого не запускается, пока в аргументах нет файла, — быстрый путь открытия
/// (constitution) платит только за один мьютекс.
/// </summary>
public sealed class SingleInstanceChannel : IDisposable
{
    private const int MaxPathLength = 4096;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    private readonly Mutex _ownership;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstanceChannel(Mutex ownership, string pipeName)
    {
        _ownership = ownership;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Пытается стать первым экземпляром. Возвращает канал, если мы первые; null — если
    /// экземпляр уже работает (тогда путь надо отправить через <see cref="TrySend"/>).
    /// </summary>
    public static SingleInstanceChannel? TryClaim(string? identity = null)
    {
        var key = BuildKey(identity);
        var mutex = new Mutex(initiallyOwned: true, $"MarkMello.{key}", out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceChannel(mutex, $"MarkMello.{key}");
    }

    /// <summary>Отправляет путь работающему экземпляру. False — не удалось, надо открыть файл самим.</summary>
    public static bool TrySend(string path, string? identity = null)
    {
        try
        {
            // CurrentUserOnly: клиент отказывается писать в канал, чей владелец — другой пользователь.
            using var client = new NamedPipeClientStream(
                ".",
                $"MarkMello.{BuildKey(identity)}",
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect((int)SendTimeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(path);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Запускает приём путей в фоне. Обработчик вызывается не на UI-потоке:
    /// переход на него — забота вызывающего.
    /// </summary>
    public void StartListening(Action<string> onPath)
    {
        ArgumentNullException.ThrowIfNull(onPath);
        _ = Task.Run(() => ListenAsync(onPath, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _ownership.Dispose();
    }

    private async Task ListenAsync(Action<string> onPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                var buffer = new char[MaxPathLength + 1];
                var read = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                var path = new string(buffer, 0, read).Trim();
                if (IsAcceptablePath(path))
                {
                    onPath(path);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Клиент оборвал соединение — ждём следующего, приложение не страдает.
            }
        }
    }

    /// <summary>
    /// Путь из канала — недоверенный ввод. Принимаются только полные локальные пути:
    /// UNC и device-пути отбрасываются до любого обращения к файловой системе, иначе
    /// сама проверка существования файла пошла бы на чужой SMB-ресурс.
    /// </summary>
    public static bool IsAcceptablePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Length <= MaxPathLength
           && Path.IsPathFullyQualified(path)
           && !(path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]));

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static string BuildKey(string? identity)
    {
        var source = $"{Environment.UserDomainName}\\{Environment.UserName}|{identity ?? Environment.ProcessPath ?? "MarkMello"}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }
}
