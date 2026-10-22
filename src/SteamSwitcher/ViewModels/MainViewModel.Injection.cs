using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels
{
    public partial class MainViewModel
    {
        [RelayCommand]
        private async Task StartInjectorAsync()
        {
            if (_disposed) return;
            if (!await _injectorGate.WaitAsync(0))
            {
                StatusText = "库连接操作正在进行，请稍候";
                return;
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _injectorAttemptCts = attempt;
            try { await StartInjectorCoreAsync(attempt.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_injectorAttemptCts, attempt)) _injectorAttemptCts = null;
                _injectorGate.Release();
            }
        }

        private async Task StartInjectorCoreAsync(CancellationToken cancellationToken)
        {
            if (_injector != null && _injector.IsConnected)
            {
                StatusText = $"注入器已连接，日志: {AppLogger.CurrentLogPath}";
                return;
            }

            var suppressPrompt = _suppressInjectorPrompt;
            _suppressInjectorPrompt = false;
            if (!suppressPrompt)
            {
                bool confirmed = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var warningResult = MessageBox.Show(
                        Application.Current.MainWindow,
                        "开启 Steam 库界面注入将启用 CEF 调试端口并向 Steam 客户端注入脚本。\n\n" +
                        "风险提示：\n" +
                        "1. 可能违反 Steam 服务条款，存在账号封禁风险\n" +
                        "2. 不建议在 VAC、竞技或反作弊敏感游戏中使用\n" +
                        "3. 可能导致 Steam 客户端不稳定\n\n" +
                        "确定要继续吗？",
                        "风险警告",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    confirmed = warningResult == MessageBoxResult.Yes;
                });

                cancellationToken.ThrowIfCancellationRequested();
                if (!confirmed)
                    return;
            }

            IsLoading = true;
            StatusText = "正在启动注入器...";
            AppLogger.Info("StartInjectorAsync requested.");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CleanupInjectorResourcesAsync();
                var webSocketPort = FindAvailablePort(8081);
                var webSocketToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _wsServer = new WebSocketServer(webSocketPort, webSocketToken);
                _wsServer.MessageReceived += OnWebSocketMessage;
                _wsServer.ClientConnected += (s, id) => AppLogger.Info($"Library WebSocket client connected: {id}");
                _wsServer.ClientDisconnected += (s, id) =>
                {
                    AppLogger.Info($"Library WebSocket client disconnected: {id}");
                    if (ReferenceEquals(s, _wsServer) && _wsServer.ClientCount == 0)
                        _injector?.NotifyControlChannelDisconnected();
                };
                await _wsServer.StartAsync();
                cancellationToken.ThrowIfCancellationRequested();
                AppLogger.Info($"Authenticated local WebSocket server started on port {webSocketPort}.");

                var debugReady = await EnsureSteamDebuggingAsync(cancellationToken);
                if (!debugReady.HasValue)
                {
                    CleanupInjectorResources();
                    StatusText = $"无法打开 Steam 调试端口，日志: {AppLogger.CurrentLogPath}";
                    return;
                }

                _injector = new SteamCEFInjector(
                    _gameBinding,
                    _accountManager,
                    debugReady.Value,
                    webSocketPort,
                    webSocketToken);
                _injector.StatusChanged += (s, msg) =>
                {
                    AppLogger.Info($"Injector status: {msg}");
                    DispatchToUi(() =>
                    {
                        if (!ReferenceEquals(s, _injector)) return;
                        IsInjectorConnected = _injector?.IsConnected == true;
                        StatusText = msg;
                    });
                };

                var connected = await _injector.ConnectAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                IsInjectorConnected = connected;
                if (!connected)
                    CleanupInjectorResources();
                StatusText = connected
                    ? $"注入器已启动，日志: {AppLogger.CurrentLogPath}"
                    : $"注入失败，日志: {AppLogger.CurrentLogPath}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CleanupInjectorResources();
                AppLogger.Info("Injector startup canceled during shutdown.");
            }
            catch (Exception ex)
            {
                CleanupInjectorResources();
                IsInjectorConnected = false;
                AppLogger.Error("StartInjectorAsync failed.", ex);
                StatusText = $"注入启动失败: {ex.Message}，日志: {AppLogger.CurrentLogPath}";
            }
            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                IsLoading = false;
            }
        }

        private async Task<int?> EnsureSteamDebuggingAsync(CancellationToken cancellationToken)
        {
            if (await IsSteamDebugPortOpenAsync(SteamDebugPort, cancellationToken))
            {
                AppLogger.Info("Steam debug port is already open.");
                return SteamDebugPort;
            }

            var steamService = _accountManager.GetSteamService();
            if (steamService.IsSteamRunning())
            {
                StatusText = "Steam 已运行但未开启调试模式，正在重启 Steam...";
                AppLogger.Info("Steam is running without debug port. Restarting with CEF debugging.");

                var closed = await steamService.CloseSteamAsync();
                if (!closed)
                {
                    AppLogger.Info("Failed to close Steam before starting debug mode.");
                    return null;
                }
            }

            var debugPort = FindAvailablePort(SteamDebugPort);
            AppLogger.Info($"Using debug port: {debugPort}");

            StatusText = "正在以调试模式启动 Steam...";
            var started = steamService.StartSteamWithDebugging(debugPort: debugPort);
            if (!started)
            {
                AppLogger.Info("StartSteamWithDebugging returned false.");
                return null;
            }

            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(1000, cancellationToken);
                if (await IsSteamDebugPortOpenAsync(debugPort, cancellationToken))
                {
                    AppLogger.Info($"Steam debug port became ready after {i + 1} seconds.");
                    await Task.Delay(1500, cancellationToken);
                    return debugPort;
                }
            }

            AppLogger.Info("Timed out waiting for Steam debug port.");
            return null;
        }

        private static async Task<bool> IsSteamDebugPortOpenAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await DebugPortProbeClient.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
                var json = response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync(cancellationToken)
                    : "";
                var ok = response.IsSuccessStatusCode &&
                         json.Contains("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase);
                AppLogger.Info($"Debug port check: status={(int)response.StatusCode}, ok={ok}");
                return ok;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"Debug port check failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }

        private static int FindAvailablePort(int startPort)
        {
            for (var port = startPort; port < startPort + 100; port++)
            {
                if (IsPortAvailable(port))
                    return port;
            }
            throw new InvalidOperationException($"未找到可用的本地端口（{startPort}-{startPort + 99}）。");
        }

        [RelayCommand]
        private async Task StopInjectorAsync()
        {
            AppLogger.Info("StopInjector requested.");
            _injectorAttemptCts?.Cancel();
            await _injectorGate.WaitAsync();
            try
            {
                await CleanupInjectorResourcesAsync();
                IsInjectorConnected = false;
                StatusText = "注入器已停止";
            }
            finally { _injectorGate.Release(); }
        }

        private Task CleanupInjectorResourcesAsync()
        {
            var injector = _injector;
            var server = _wsServer;
            _injector = null;
            _wsServer = null;
            return Task.Run(() =>
            {
                try { injector?.Dispose(); }
                catch (Exception ex) { AppLogger.Warn($"Failed to dispose Steam CEF injector: {ex.Message}"); }
                try { server?.Dispose(); }
                catch (Exception ex) { AppLogger.Warn($"Failed to dispose library WebSocket server: {ex.Message}"); }
            });
        }

        private void CleanupInjectorResources()
        {
            try
            {
                _injector?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to dispose Steam CEF injector: {ex.Message}");
            }
            _injector = null;

            try
            {
                _wsServer?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to dispose library WebSocket server: {ex.Message}");
            }
            _wsServer = null;
        }

        private void OnWebSocketMessage(object? sender, (string action, System.Text.Json.JsonElement data) e)
        {
            if (!ReferenceEquals(sender, _wsServer)) return;
            if (e.action == "hello")
            {
                if (e.data.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == System.Text.Json.JsonValueKind.String)
                    _injector?.ConfirmHandshake(sessionId.GetString());
                return;
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            if (!dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(new Action(() => OnWebSocketMessage(sender, e)));
                return;
            }

            _ = HandleWebSocketMessageAsync(e);
        }

        private async Task HandleWebSocketMessageAsync((string action, System.Text.Json.JsonElement data) e)
        {
            try
            {
                AppLogger.Info($"WebSocket action received: {e.action}");

                switch (e.action)
                {
                    case "log":
                        if (e.data.TryGetProperty("message", out var msg))
                        {
                            AppLogger.Info($"Library script: {msg.GetString()}");
                        }
                        break;

                    case "switchAndLaunch":
                        if (e.data.TryGetProperty("appId", out var appIdElement))
                        {
                            var appId = appIdElement.GetInt32();
                            var gameName = e.data.TryGetProperty("gameName", out var nameEl)
                                ? nameEl.GetString() ?? ""
                                : "";
                            var steamId = e.data.TryGetProperty("steamId", out var steamIdEl)
                                ? steamIdEl.GetString()
                                : null;

                            await SwitchAccountAndLaunchGameAsync(appId, gameName, steamId);
                        }
                        break;

                    case "setBinding":
                        if (e.data.TryGetProperty("appId", out var bindAppId) &&
                            e.data.TryGetProperty("gameName", out var bindGameName) &&
                            e.data.TryGetProperty("steamId", out var bindSteamId) &&
                            e.data.TryGetProperty("accountName", out var bindAccountName))
                        {
                            await _gameBinding.SetBindingAsync(
                                bindAppId.GetInt32(),
                                bindGameName.GetString() ?? "",
                                bindSteamId.GetString() ?? "",
                                bindAccountName.GetString() ?? "");
                            AppLogger.Info($"Binding saved from library: appId={bindAppId.GetInt32()}, account={bindAccountName.GetString()}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebSocket message handling failed.", ex);
                System.Diagnostics.Debug.WriteLine($"WebSocket message error: {ex.Message}");
            }
        }

    }
}
