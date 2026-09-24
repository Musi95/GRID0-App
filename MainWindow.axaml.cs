using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using GRID0.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using Monitor = GRID0.Core.Monitor;

namespace GRID0;

public partial class MainWindow : Window
{
    private bool _running;
    private bool _reconnectMode;
    private CancellationTokenSource? _monitorCts;

    public MainWindow()
    {
        InitializeComponent();
        ActionButton.Click += async (_, _) => await OnActionAsync();
        EmuButton.Click += async (_, _) => await OnConfigureEmulatorsAsync();
        Opened += async (_, _) => await RunSetupAsync();
        Closing += (_, _) => _monitorCts?.Cancel();
    }

    private async Task OnActionAsync()
    {
        _monitorCts?.Cancel();
        await RunSetupAsync();
    }

    private async Task RunSetupAsync()
    {
        if (_running) return;
        _running = true;
        _reconnectMode = false;
        Dispatcher.UIThread.Post(() =>
        {
            ActionButton.IsEnabled = false;
            ActionButton.Content = "Retry";
            EmuButton.IsEnabled = false;
            Progress.IsVisible = true;
        });
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;

        try
        {
            SetStatus("gray", "Checking for ZeroTier...");
            if (!await ZeroTier.IsHealthyAsync())
            {
                if (ZeroTier.FindCli() is not null)
                    Log("Found a ZeroTier install, but it is not responding. Repairing...");
                SetStatus("gray", "Installing ZeroTier...");
                await ZeroTier.InstallAsync(Log);
                Log("Waiting for the ZeroTier service...");
                if (!await ZeroTier.WaitForCliAsync())
                    throw new Exception("ZeroTier installed, but its service did not start.");
                Log("ZeroTier is running.");
            }
            else
            {
                Log("ZeroTier is already installed.");
            }

            if (!await WaitForNodeOnlineAsync(ct))
                throw new Exception("ZeroTier did not come online. Turn your WiFi on and check your internet connection, then press Retry.");

            SetStatus("gray", "Joining the GRID0 network...");
            var (jcode, jmsg) = await ZeroTier.JoinAsync();
            if (jmsg.Length > 0) Log(jmsg);
            if (jcode != 0)
                throw new Exception("Could not join the network. " + jmsg);

            if (!await WaitForFirstConnectionAsync(ct))
            {
                SetStatus("orange", "Still not connected",
                    "Timed out waiting for the network. Press Retry to try again.");
                return;
            }

            // Connected. The emulator button needs the ZeroTier adapter,
            // which exists from here on.
            Dispatcher.UIThread.Post(() => EmuButton.IsEnabled = true);

            // Keep watching in the background so any later disconnect
            // updates the status instead of leaving a stale green dot.
            _ = MonitorLoopAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("red", "Something went wrong", ex.Message);
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                Progress.IsVisible = false;
                ActionButton.IsEnabled = true;
                _running = false;
            });
        }
    }

    private async Task<bool> WaitForNodeOnlineAsync(CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = await Monitor.GetFullStateAsync();
            if (s is { CliOk: true, NodeOnline: true, HasInternet: true }) return true;
            if (s.CliOk && !s.HasInternet)
                SetStatus("red", "No internet connection",
                    "Turn your WiFi on. The app will continue automatically.");
            else
                SetStatus("gray", "Waiting for the ZeroTier service...");
            await Task.Delay(3000, ct);
        }
        return false;
    }

    // Waits up to ~3 minutes for the first GRID0 connection, using the
    // same full state tree as the background monitor.
    private async Task<bool> WaitForFirstConnectionAsync(CancellationToken ct)
    {
        string? lastKind = null;
        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = await Monitor.GetFullStateAsync();
            var (kind, title, detail, connected) = Monitor.Decide(s);

            if (kind != lastKind)
            {
                lastKind = kind;
                if (kind == "gray" && s.NetStatus is null && s is { CliOk: true, NodeOnline: true, HasInternet: true })
                    Log("Not on the GRID0 network, joining...");
                else if (s.NetStatus is not null)
                    Log("Network status: " + s.NetStatus);
            }
            // During setup a vanished network entry means "join again",
            // not "disconnected": the user has not left anything yet.
            if (kind == "gray" && s.NetStatus is null && s is { CliOk: true, NodeOnline: true, HasInternet: true })
            {
                SetStatus("gray", "Joining the GRID0 network...");
                await ZeroTier.JoinAsync();
            }
            else
            {
                SetStatus(kind, title, detail);
            }

            if (connected) return true;
            await Task.Delay(3000, ct);
        }
        return false;
    }

    // Background watch after the first connection. Every poll runs the
    // full disconnect decision tree: internet, service, node, membership,
    // adapter state, and managed address. A vanished network entry is
    // debounced (2 polls) so a transient blip does not flash, then shows
    // disconnected with Reconnect. Never silently re-joins: fighting the
    // user's own leave is hostile UX.
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var nullStreak = 0;
        try
        {
            while (true)
            {
                await Task.Delay(3000, ct);
                var s = await Monitor.GetFullStateAsync();

                if (s.NetStatus is null && s is { CliOk: true, NodeOnline: true, HasInternet: true })
                {
                    nullStreak++;
                    if (nullStreak >= 2 && !_reconnectMode)
                    {
                        _reconnectMode = true;
                        SetStatus("gray", "Disconnected from GRID0",
                            "You left the GRID0 network. Press Reconnect to join again.");
                        Dispatcher.UIThread.Post(() => ActionButton.Content = "Reconnect");
                        Log("Disconnected from the GRID0 network.");
                    }
                    continue;
                }
                nullStreak = 0;

                var (kind, title, detail, _) = Monitor.Decide(s);
                if (_reconnectMode)
                {
                    _reconnectMode = false;
                    Dispatcher.UIThread.Post(() => ActionButton.Content = "Retry");
                }
                SetStatus(kind, title, detail);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("red", "Something went wrong", ex.Message);
            Log("ERROR: " + ex.Message);
        }
    }

    private async Task OnConfigureEmulatorsAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            EmuButton.IsEnabled = false;
            ActionButton.IsEnabled = false;
        });
        try
        {
            Log("Scanning for emulators...");
            int n = await Emulators.ConfigureAsync(Log, PromptCloseEmulatorAsync);
            Log(n > 0 ? "Emulator setup done." : "Emulator setup done, nothing was changed.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                EmuButton.IsEnabled = true;
                ActionButton.IsEnabled = true;
            });
        }
    }

    private Task<bool> PromptCloseEmulatorAsync(string displayName)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { tcs.SetResult(await ShowCloseDialogAsync(displayName)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private async Task<bool> ShowCloseDialogAsync(string displayName)
    {
        var dlg = new Window
        {
            Title = "GRID0",
            Width = 400,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = displayName + " is running and must be closed before its config can be edited " +
                   "(emulators overwrite their config file when they exit).\n\nClose it now, then press OK.",
            TextWrapping = TextWrapping.Wrap,
        });
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        var skip = new Button { Content = "Skip", Width = 80 };
        var ok = new Button { Content = "OK", Width = 80 };
        skip.Click += (_, _) => dlg.Close(false);
        ok.Click += (_, _) => dlg.Close(true);
        row.Children.Add(skip);
        row.Children.Add(ok);
        panel.Children.Add(row);
        dlg.Content = panel;
        return await dlg.ShowDialog<bool>(this);
    }

    private void SetStatus(string kind, string title, string detail = "")
    {
        var color = kind switch
        {
            "green" => Colors.Green,
            "orange" => Colors.Orange,
            "red" => Colors.Red,
            _ => Colors.Gray,
        };
        Dispatcher.UIThread.Post(() =>
        {
            Dot.Fill = new SolidColorBrush(color);
            StatusLabel.Text = title;
            DetailLabel.Text = detail;
        });
    }

    private void Log(string message)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }
}
