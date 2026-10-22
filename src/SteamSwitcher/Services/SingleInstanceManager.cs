using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

namespace SteamSwitcher.Services
{
    public sealed class SingleInstanceManager : IDisposable
    {
        private readonly Mutex _applicationMutex;
        private readonly Mutex? _legacyUserMutex;
        // 不能是 readonly：释放互斥体后必须立刻把标记改回 false。
        // 否则一旦将来只保留其中一个互斥体（或调整 IsPrimaryInstance 的判定），
        // Dispose 会对已释放的句柄二次 ReleaseMutex 并抛出同步异常。
        private bool _ownsApplicationMutex;
        private bool _ownsLegacyUserMutex;
        private readonly int _ownerThreadId;
        private readonly string _pipeName;
        private readonly string _nonceFilePath;
        private readonly object _lifecycleLock = new();
        private readonly CancellationTokenSource _cancellation = new();
        private Action<string[]>? _activate;
        private Task? _listenerTask;
        private bool _disposed;

        // Application-layer handshake secret. Same-user processes can open the
        // named pipe, so the payload is only accepted when it carries this nonce.
        private readonly string _instanceNonce = string.Empty;
        private static string DefaultNonceFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamSwitch", "instance-nonce.txt");

        private sealed class PipeActivationMessage
        {
            [System.Text.Json.Serialization.JsonPropertyName("token")]
            public string? Token { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("args")]
            public string[]? Args { get; set; }
        }

        public bool IsPrimaryInstance => _ownsApplicationMutex && _ownsLegacyUserMutex;

        public SingleInstanceManager()
            : this(@"Local\SteamSwitch.App", GetLegacyMutexName(), GetPipeName(), DefaultNonceFilePath)
        {
        }

        internal SingleInstanceManager(string applicationMutexName, string userMutexName, string pipeName,
            string? nonceFilePath = null)
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _pipeName = pipeName;
            _nonceFilePath = nonceFilePath == null
                ? GetIsolatedNonceFilePath(applicationMutexName, userMutexName, pipeName)
                : Path.GetFullPath(nonceFilePath);
            _applicationMutex = new Mutex(false, applicationMutexName);
            try
            {
                _ownsApplicationMutex = TryAcquireMutex(_applicationMutex);
                if (_ownsApplicationMutex)
                {
                    _legacyUserMutex = new Mutex(false, userMutexName);
                    _ownsLegacyUserMutex = TryAcquireMutex(_legacyUserMutex);
                }
            }
            catch
            {
                if (_ownsApplicationMutex) _applicationMutex.ReleaseMutex();
                _legacyUserMutex?.Dispose();
                _applicationMutex.Dispose();
                _cancellation.Dispose();
                throw;
            }

            // Keep the legacy per-user mutex during the transition from v2.7 so a
            // newly installed copy cannot coexist with an older copy that already
            // implements the original single-instance protocol. The stable mutex
            // prevents future versions in different folders from drifting apart.
            if (!IsPrimaryInstance)
            {
                if (_ownsApplicationMutex)
                {
                    _applicationMutex.ReleaseMutex();
                    _ownsApplicationMutex = false;
                }
                if (_ownsLegacyUserMutex)
                {
                    _legacyUserMutex!.ReleaseMutex();
                    _ownsLegacyUserMutex = false;
                }
                _instanceNonce = "";
            }
            else
            {
                _instanceNonce = CreateAndPersistNonce();
            }
        }

        internal string NoncePath => _nonceFilePath;

        private static bool TryAcquireMutex(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                // WaitOne transfers ownership even when the previous owner died.
                return true;
            }
        }

        private static string GetIsolatedNonceFilePath(string applicationMutexName, string userMutexName, string pipeName)
        {
            var identity = applicationMutexName + "\n" + userMutexName + "\n" + pipeName;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return Path.Combine(Path.GetTempPath(), "SteamSwitch", "instances", hash, "instance-nonce.txt");
        }

        private string CreateAndPersistNonce()
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                var directory = Path.GetDirectoryName(_nonceFilePath)!;
                Directory.CreateDirectory(directory);
                // Restrict to the current user as far as the framework allows on
                // this platform; FileShare.Read lets secondary instances read it.
                File.WriteAllText(_nonceFilePath, nonce);
                return nonce;
            }
            catch (Exception ex)
            {
                // Returning empty lets ParseActivationCommand accept legacy
                // "activate" / JSON-array payloads, so a secondary that also
                // cannot read the nonce file can still activate the primary.
                AppLogger.Warn($"Unable to persist single-instance nonce: {ex.Message}. Falling back to legacy activation.");
                return string.Empty;
            }
        }

        private string? TryReadPrimaryNonce()
        {
            try
            {
                if (!File.Exists(_nonceFilePath))
                    return null;
                return File.ReadAllText(_nonceFilePath).Trim();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to read single-instance nonce: {ex.Message}");
                return null;
            }
        }

        private bool IsAuthorizedToken(string? supplied)
        {
            if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(_instanceNonce))
                return false;
            var expectedBytes = Encoding.UTF8.GetBytes(_instanceNonce);
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
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
            ArgumentNullException.ThrowIfNull(activate);
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!IsPrimaryInstance)
                    throw new InvalidOperationException("Only the primary instance can listen for activation.");
                if (_listenerTask != null)
                    throw new InvalidOperationException("The activation listener has already started.");
                _activate = activate;
                _listenerTask = Task.Run(ListenAsync);
            }
        }

        public bool SignalPrimaryInstance(string[]? arguments = null)
        {
            var token = TryReadPrimaryNonce();
            string payload;
            if (string.IsNullOrEmpty(token))
            {
                // Nonce file missing or unreadable (primary write failed, or
                // an older install). Fall back to the legacy activation format
                // that a primary with an empty _instanceNonce still accepts.
                payload = arguments is { Length: > 0 }
                    ? JsonSerializer.Serialize(arguments)
                    : "activate";
            }
            else
            {
                payload = JsonSerializer.Serialize(new PipeActivationMessage
                {
                    Token = token,
                    Args = arguments ?? Array.Empty<string>()
                });
            }

            var deadline = DateTime.UtcNow.AddSeconds(8);
            do
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(500);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine(payload);
                    return true;
                }
                catch (TimeoutException)
                {
                    Thread.Sleep(150);
                }
                catch (IOException)
                {
                    Thread.Sleep(150);
                }
            }
            while (DateTime.UtcNow < deadline);

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
                        var arguments = ParseActivationCommand(command);
                        if (arguments != null)
                            _activate?.Invoke(arguments);
                        else
                            AppLogger.Warn("Rejected single-instance command with invalid token or format.");
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
                // Any other exception (UnauthorizedAccessException from a racing
                // client, ObjectDisposedException during shutdown races, etc.)
                // must not fault the listener task permanently.
                catch (Exception ex) when (!_cancellation.IsCancellationRequested)
                {
                    AppLogger.Error("Single-instance pipe listener recovered from an unexpected error.", ex);
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
        }

        /// <summary>
        /// Returns the activation arguments when the command is authorized,
        /// or null when it must be rejected.
        /// </summary>
        private string[]? ParseActivationCommand(string command)
        {
            // Legacy clients: bare "activate" string or a raw string[] array.
            // Accepted only when a nonce is unavailable (should not happen for
            // a healthy primary) so old secondaries still work during upgrades.
            if (string.Equals(command, "activate", StringComparison.Ordinal))
                return string.IsNullOrEmpty(_instanceNonce) ? Array.Empty<string>() : null;

            if (command.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(_instanceNonce))
                    return null;
                return JsonSerializer.Deserialize<string[]>(command) ?? Array.Empty<string>();
            }

            PipeActivationMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<PipeActivationMessage>(command);
            }
            catch (JsonException)
            {
                return null;
            }

            if (message == null)
                return null;
            if (!IsAuthorizedToken(message.Token))
                return null;

            return message.Args ?? Array.Empty<string>();
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;
                _activate = null;
            }
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
                // Remove only our own token while both mutexes are still held.
                // Releasing first lets a replacement write a new nonce that an
                // exiting instance could otherwise delete.
                try
                {
                    if (!string.IsNullOrEmpty(_instanceNonce) && File.Exists(_nonceFilePath) &&
                        string.Equals(File.ReadAllText(_nonceFilePath).Trim(), _instanceNonce, StringComparison.Ordinal))
                        File.Delete(_nonceFilePath);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Unable to remove single-instance nonce file: {ex.Message}");
                }
                if (Environment.CurrentManagedThreadId == _ownerThreadId)
                {
                    if (_ownsLegacyUserMutex)
                    {
                        _legacyUserMutex!.ReleaseMutex();
                        _ownsLegacyUserMutex = false;
                    }
                    if (_ownsApplicationMutex)
                    {
                        _applicationMutex.ReleaseMutex();
                        _ownsApplicationMutex = false;
                    }
                }
                else
                {
                    AppLogger.Warn("Single-instance mutex handles were disposed from a non-owner thread.");
                }
            }
            _legacyUserMutex?.Dispose();
            _applicationMutex.Dispose();
            if (_listenerTask == null || _listenerTask.IsCompleted)
                _cancellation.Dispose();
            else
                _ = _listenerTask.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
