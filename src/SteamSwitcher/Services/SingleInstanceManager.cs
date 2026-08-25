using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace SteamSwitcher.Services
{
    public sealed class SingleInstanceManager : IDisposable
    {
        private readonly Mutex _applicationMutex;
        private readonly Mutex _legacyUserMutex;
        private readonly bool _ownsApplicationMutex;
        private readonly bool _ownsLegacyUserMutex;
        private readonly int _ownerThreadId;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cancellation = new();
        private Action<string[]>? _activate;
        private Task? _listenerTask;
        private bool _disposed;

        public bool IsPrimaryInstance => _ownsApplicationMutex && _ownsLegacyUserMutex;

        public SingleInstanceManager()
            : this(@"Local\SteamSwitch.App", GetLegacyMutexName(), GetPipeName())
        {
        }

        internal SingleInstanceManager(string applicationMutexName, string userMutexName, string pipeName)
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _pipeName = pipeName;
            _applicationMutex = new Mutex(true, applicationMutexName, out _ownsApplicationMutex);
            _legacyUserMutex = new Mutex(true, userMutexName, out _ownsLegacyUserMutex);

            // Keep the legacy per-user mutex during the transition from v2.7 so a
            // newly installed copy cannot coexist with an older copy that already
            // implements the original single-instance protocol. The stable mutex
            // prevents future versions in different folders from drifting apart.
            if (!IsPrimaryInstance)
            {
                if (_ownsApplicationMutex)
                    _applicationMutex.ReleaseMutex();
                if (_ownsLegacyUserMutex)
                    _legacyUserMutex.ReleaseMutex();
            }
        }

        private static string GetCurrentUserInstanceId()
        {
            var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"SteamSwitch.{userId}";
        }

        private static string GetLegacyMutexName() => $@"Local\{GetCurrentUserInstanceId()}";
        private static string GetPipeName() => GetCurrentUserInstanceId();

        public void StartListening(Action<string[]> activate)
        {
            if (!IsPrimaryInstance)
                throw new InvalidOperationException("Only the primary instance can listen for activation.");

            _activate = activate;
            _listenerTask = Task.Run(ListenAsync);
        }

        public bool SignalPrimaryInstance(string[]? arguments = null)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(300);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine(JsonSerializer.Serialize(arguments ?? Array.Empty<string>()));
                    return true;
                }
                catch (TimeoutException)
                {
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }

            return false;
        }

        private async Task ListenAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync(_cancellation.Token);
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        var arguments = string.Equals(command, "activate", StringComparison.Ordinal)
                            ? Array.Empty<string>()
                            : JsonSerializer.Deserialize<string[]>(command) ?? Array.Empty<string>();
                        _activate?.Invoke(arguments);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    if (!_cancellation.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(100, _cancellation.Token);
                        }
                        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    AppLogger.Warn($"Ignored malformed single-instance command: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // Cancellation is expected while shutting down.
            }

            if (IsPrimaryInstance)
            {
                if (Environment.CurrentManagedThreadId == _ownerThreadId)
                {
                    _legacyUserMutex.ReleaseMutex();
                    _applicationMutex.ReleaseMutex();
                }
                else
                {
                    AppLogger.Warn("Single-instance mutex handles were disposed from a non-owner thread.");
                }
            }
            _legacyUserMutex.Dispose();
            _applicationMutex.Dispose();
            _cancellation.Dispose();
        }
    }
}
