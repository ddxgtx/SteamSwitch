using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace SteamSwitcher.Services
{
    public sealed class SingleInstanceManager : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _ownsMutex;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cancellation = new();
        private Action? _activate;
        private Task? _listenerTask;

        public bool IsPrimaryInstance => _ownsMutex;

        public SingleInstanceManager()
        {
            var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var instanceId = $"SteamSwitch.{userId}";
            _pipeName = instanceId;
            _mutex = new Mutex(true, $@"Local\{instanceId}", out _ownsMutex);
        }

        public void StartListening(Action activate)
        {
            if (!_ownsMutex)
                throw new InvalidOperationException("Only the primary instance can listen for activation.");

            _activate = activate;
            _listenerTask = Task.Run(ListenAsync);
        }

        public bool SignalPrimaryInstance()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(300);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine("activate");
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
                    if (string.Equals(command, "activate", StringComparison.Ordinal))
                        _activate?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    if (!_cancellation.IsCancellationRequested)
                        await Task.Delay(100, _cancellation.Token);
                }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // Cancellation is expected while shutting down.
            }

            if (_ownsMutex)
                _mutex.ReleaseMutex();
            _mutex.Dispose();
            _cancellation.Dispose();
        }
    }
}
