using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace BPTrainer;

public partial class MainWindow : Window
{
    private BpState _state;
    private BpConfig _config;
    private MqttClientWrapper _mqtt;
    private List<HeroData> _heroes = new();
    private DispatcherTimer? _timer;
    private int _remainingSeconds;
    private int _backupSeconds;
    private bool _isBackupMode;
    private bool _isLocalTurn;
    private bool _systemBansInitialized;
    private Random _random = new();
    private BpSide _localSide = BpSide.Blue;
    private bool _bothPlayersReady;

    public MainWindow()
    {
        InitializeComponent();

        _config = new BpConfig();
        _state = new BpState(_config);
        _mqtt = new MqttClientWrapper();
        _backupSeconds = _config.BackupTimerSeconds;

        _mqtt.ConnectionStateChanged += OnConnectionStateChanged;
        _mqtt.ActionReceived += OnActionReceived;
        _mqtt.SnapshotRequested += OnSnapshotRequested;
        _mqtt.SnapshotReceived += OnSnapshotReceived;
        _mqtt.PlayerJoined += OnPlayerJoined;

        LoadHeroes();
        BuildHeroGrid();
        UpdateUI();
    }

    private void LoadHeroes()
    {
        try
        {
            var possiblePaths = new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "heroes.json"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "heroes.json"),
                "heroes.json"
            };

            foreach (var heroFile in possiblePaths)
            {
                if (System.IO.File.Exists(heroFile))
                {
                    var json = System.IO.File.ReadAllText(heroFile);
                    _heroes = JsonSerializer.Deserialize<List<HeroData>>(json) ?? new List<HeroData>();
                    if (_heroes.Count > 0)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            _heroes = new List<HeroData>();
        }

        if (_heroes.Count == 0)
        {
            for (int i = 1; i <= 20; i++)
            {
                _heroes.Add(new HeroData($"hero_{i}", $"英雄{i}", $"assets/hero_{i}.png"));
            }
        }
    }

    private void BuildHeroGrid()
    {
        HeroGrid.Children.Clear();

        var rows = _heroes.GroupBy(h => h.Row).OrderBy(g => g.Key);

        foreach (var rowGroup in rows)
        {
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4
            };

            foreach (var hero in rowGroup)
            {
                var border = new Border
                {
                    Width = 72,
                    Height = 72,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse("#1A2332")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A3A4A")),
                    BorderThickness = new Thickness(1),
                    Tag = hero,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, hero.ImagePath);
                if (System.IO.File.Exists(imagePath))
                {
                    var img = new Image
                    {
                        Source = new Bitmap(imagePath),
                        Stretch = Stretch.UniformToFill
                    };
                    Grid.SetRow(img, 0);
                    grid.Children.Add(img);
                }

                var nameText = new TextBlock
                {
                    Text = hero.HeroName,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(2, 2, 2, 4)
                };
                Grid.SetRow(nameText, 1);
                grid.Children.Add(nameText);

                border.Child = grid;

                border.PointerEntered += (s, e) =>
                {
                    if (border.Opacity > 0.9)
                        border.BorderBrush = new SolidColorBrush(Color.Parse("#FFD700"));
                };

                border.PointerExited += (s, e) =>
                {
                    if (border.Opacity > 0.9)
                        border.BorderBrush = new SolidColorBrush(Color.Parse("#2A3A4A"));
                };

                border.PointerPressed += (s, e) => OnHeroClick(hero);

                rowPanel.Children.Add(border);
            }

            HeroGrid.Children.Add(rowPanel);
        }
    }

    private void InitializeSystemBans()
    {
        if (_systemBansInitialized) return;

        var seed = 0;
        if (int.TryParse(_mqtt.RoomId, out var roomNum))
            seed = roomNum;

        var seededRandom = new Random(seed);
        var available = _heroes.OrderBy(h => h.HeroId).ToList();
        var shuffled = available.OrderBy(_ => seededRandom.Next()).Take(_config.SystemBanCount).ToList();

        foreach (var hero in shuffled)
        {
            _state.SystemBans.Add(hero.HeroId);
        }

        _systemBansInitialized = true;
        UpdateHeroStates();
        StatusBar.Text = $"系统已随机禁用{_config.SystemBanCount}个单位";
    }

    private void OnHeroClick(HeroData hero)
    {
        if (_mqtt.State != ConnectionState.Connected)
        {
            StatusBar.Text = "未连接到房间";
            return;
        }

        if (_state.IsFinished)
        {
            StatusBar.Text = "对局已结束";
            return;
        }

        if (!_isLocalTurn)
        {
            StatusBar.Text = "等待对方操作";
            return;
        }

        if (_state.IsHeroUnavailableForSide(hero.HeroId, _state.CurrentSide))
        {
            StatusBar.Text = "该单位已被禁用或你方已选过";
            return;
        }

        var actionType = _state.GetCurrentActionType();
        var action = new BpAction
        {
            Side = _state.CurrentSide,
            Type = actionType,
            HeroId = hero.HeroId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var (valid, error) = _state.ValidateAction(action);
        if (!valid)
        {
            StatusBar.Text = $"操作无效: {error}";
            return;
        }

        StopTimer();
        _state.ApplyAction(action);
        _ = _mqtt.SendActionAsync(action);

        _isLocalTurn = _state.CurrentSide == _localSide;
        UpdateUI();
        UpdateHeroStates();

        if (!_state.IsFinished)
        {
            if (_isLocalTurn)
                StartTimer();
            else
                StatusBar.Text = "等待对方操作";
        }
        else
        {
            ExportBtn.IsVisible = true;
            ReplayBtn.IsVisible = true;
            StatusBar.Text = "对局结束";
        }
    }

    private async void OnConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var roomId = RoomIdInput.Text?.Trim();
        if (string.IsNullOrEmpty(roomId) || roomId.Length != 4 || !int.TryParse(roomId, out _))
        {
            StatusBar.Text = "请输入4位数字房间号";
            return;
        }

        _localSide = SideSelector.SelectedIndex == 0 ? BpSide.Blue : BpSide.Red;

        ConnectBtn.IsEnabled = false;
        SideSelector.IsEnabled = false;
        StatusBar.Text = "正在连接...";

        try
        {
            await _mqtt.ConnectAsync(roomId);
        }
        catch
        {
            StatusBar.Text = "连接失败";
            ConnectBtn.IsEnabled = true;
            SideSelector.IsEnabled = true;
        }
    }

    private async void OnDisconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _mqtt.DisconnectAsync();
        StopTimer();
        _state = new BpState(_config);
        _isLocalTurn = false;
        _bothPlayersReady = false;
        _systemBansInitialized = false;
        _backupSeconds = _config.BackupTimerSeconds;
        ConnectBtn.IsEnabled = true;
        ConnectBtn.IsVisible = true;
        DisconnectBtn.IsVisible = false;
        ExportBtn.IsVisible = false;
        ReplayBtn.IsVisible = false;
        StatusBar.Text = "已断开";
        UpdateUI();
        UpdateHeroStates();
    }

    private void OnConnectionStateChanged(ConnectionState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (state)
            {
                case ConnectionState.Connected:
                    ConnectionDot.Background = new SolidColorBrush(Color.Parse("#00FF00"));
                    ConnectionStatus.Text = "已连接";
                    ConnectBtn.IsVisible = false;
                    DisconnectBtn.IsVisible = true;
                    ConnectBtn.IsEnabled = true;
                    StatusBar.Text = $"已连接到房间 - 你是{(_localSide == BpSide.Blue ? "蓝色方" : "红色方")}，等待对方加入...";
                    InitializeSystemBans();
                    _ = _mqtt.SendJoinAsync(_localSide);
                    break;

                case ConnectionState.Connecting:
                    ConnectionDot.Background = new SolidColorBrush(Color.Parse("#FFAA00"));
                    ConnectionStatus.Text = "连接中...";
                    break;

                case ConnectionState.Disconnected:
                    ConnectionDot.Background = new SolidColorBrush(Color.Parse("#FF0000"));
                    ConnectionStatus.Text = "未连接";
                    StopTimer();
                    _isLocalTurn = false;
                    break;
            }
            UpdateUI();
        });
    }

    private void OnPlayerJoined(string senderId, BpSide side)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_bothPlayersReady) return;

            _bothPlayersReady = true;
            _ = _mqtt.SendJoinAsync(_localSide);

            StatusBar.Text = "双方已就绪，开始BP！";
            _isLocalTurn = _state.CurrentSide == _localSide;
            UpdateUI();

            if (_isLocalTurn)
                StartTimer();
        });
    }

    private void OnActionReceived(BpAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_state.ActionHistory.Any(a => a.Side == action.Side && a.Type == action.Type && a.HeroId == action.HeroId))
            {
                return;
            }

            var (valid, error) = _state.ValidateAction(action);
            if (!valid)
            {
                StatusBar.Text = $"拒绝无效操作: {error}";
                return;
            }

            _state.ApplyAction(action);
            StopTimer();
            _isLocalTurn = _state.CurrentSide == _localSide;
            UpdateUI();
            UpdateHeroStates();

            if (!_state.IsFinished)
            {
                if (_isLocalTurn)
                {
                    StartTimer();
                }
                else
                {
                    StatusBar.Text = "等待对方操作";
                }
            }
            else
            {
                ExportBtn.IsVisible = true;
                ReplayBtn.IsVisible = true;
                StatusBar.Text = "对局结束";
            }
        });
    }

    private void OnSnapshotRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var snapshot = _state.GetSnapshot();
            _ = _mqtt.SendSnapshotAsync(snapshot);
        });
    }

    private void OnSnapshotReceived(BpStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _state.LoadSnapshot(snapshot);
            _systemBansInitialized = _state.SystemBans.Count > 0;
            _isLocalTurn = _state.CurrentSide == _localSide;
            UpdateUI();
            UpdateHeroStates();

            if (!_state.IsFinished)
            {
                if (_isLocalTurn)
                {
                    StartTimer();
                }
                else
                {
                    StatusBar.Text = "等待对方操作";
                }
            }
            else
            {
                ExportBtn.IsVisible = true;
                ReplayBtn.IsVisible = true;
            }
        });
    }

    private void UpdateUI()
    {
        UpdateSlot("BlueBan", _state.BlueBans, 2, true);
        UpdateSlot("RedBan", _state.RedBans, 2, true);
        UpdateSlot("BluePick", _state.BluePicks, 5, false);
        UpdateSlot("RedPick", _state.RedPicks, 5, false);

        if (_state.IsFinished)
        {
            TurnIndicator.Text = "对局结束";
            TurnIndicator.Foreground = new SolidColorBrush(Color.Parse("#8899AA"));
            StepIndicator.Text = "";
        }
        else if (_state.CurrentStep < _config.TotalSteps)
        {
            var sideText = _state.CurrentSide == BpSide.Blue ? "蓝色方" : "红色方";
            var actionText = _state.GetCurrentActionType() == BpActionType.Ban ? "BAN" : "PICK";

            TurnIndicator.Text = $"{sideText} {actionText}";
            TurnIndicator.Foreground = _state.CurrentSide == BpSide.Blue
                ? new SolidColorBrush(Color.Parse("#4A9EFF"))
                : new SolidColorBrush(Color.Parse("#FF4A4A"));
            StepIndicator.Text = $"第 {_state.CurrentStep + 1}/{_config.TotalSteps} 步";
        }
    }

    private void UpdateSlot(string prefix, List<string> heroIds, int maxCount, bool isBan)
    {
        for (int i = 0; i < maxCount; i++)
        {
            var border = this.FindControl<Border>($"{prefix}{i}");
            if (border == null) continue;

            border.Child = null;

            if (i < heroIds.Count)
            {
                var heroId = heroIds[i];
                var hero = _heroes.FirstOrDefault(h => h.HeroId == heroId);
                if (hero != null)
                {
                    var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, hero.ImagePath);
                    if (System.IO.File.Exists(imagePath))
                    {
                        var img = new Image
                        {
                            Source = new Bitmap(imagePath),
                            Stretch = Stretch.UniformToFill
                        };
                        border.Child = img;
                    }
                    else
                    {
                        border.Child = new TextBlock
                        {
                            Text = hero.HeroName,
                            FontSize = 10,
                            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap
                        };
                    }

                    if (isBan)
                    {
                        border.Opacity = 0.5;
                    }
                }
            }
            else
            {
                border.Opacity = 1.0;
            }
        }

        for (int i = maxCount; i < 5; i++)
        {
            var border = this.FindControl<Border>($"{prefix}{i}");
            if (border != null)
            {
                border.IsVisible = false;
            }
        }
    }

    private void UpdateHeroStates()
    {
        foreach (var rowPanel in HeroGrid.Children)
        {
            if (rowPanel is not StackPanel row) continue;
            foreach (var child in row.Children)
            {
                if (child is Border border && border.Tag is HeroData hero)
                {
                    if (_state.IsHeroUnavailableForSide(hero.HeroId, _localSide))
                    {
                        border.Opacity = 0.3;
                        border.IsHitTestVisible = false;
                    }
                    else
                    {
                        border.Opacity = 1.0;
                        border.IsHitTestVisible = true;
                    }
                }
            }
        }
    }

    private void StartTimer()
    {
        if (!_bothPlayersReady) return;

        StopTimer();

        _isLocalTurn = true;
        _isBackupMode = false;
        _remainingSeconds = _config.ActionTimerSeconds;
        TimerText.Text = _remainingSeconds.ToString();
        TimerBorder.BorderBrush = new SolidColorBrush(Color.Parse("#FFD700"));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            if (_backupSeconds > 0)
            {
                _isBackupMode = true;
                _backupSeconds--;
                _remainingSeconds = 1;
                TimerText.Text = _backupSeconds.ToString();
                TimerBorder.BorderBrush = new SolidColorBrush(Color.Parse("#FF8C00"));
                StatusBar.Text = $"备用时间: {_backupSeconds}秒";
            }
            else
            {
                StopTimer();
                HandleTimeout();
                return;
            }
        }
        else if (_isBackupMode)
        {
            TimerText.Text = _backupSeconds.ToString();
            StatusBar.Text = $"备用时间: {_backupSeconds}秒";
        }
        else
        {
            TimerText.Text = _remainingSeconds.ToString();
        }

        if (_remainingSeconds <= 5 && !_isBackupMode)
        {
            TimerBorder.BorderBrush = new SolidColorBrush(Color.Parse("#FF4A4A"));
        }
    }

    private void HandleTimeout()
    {
        StatusBar.Text = "超时，系统自动操作";

        var available = _heroes.Where(h => !_state.IsHeroUnavailableForSide(h.HeroId, _state.CurrentSide)).ToList();
        if (available.Count == 0) return;

        var randomHero = available[_random.Next(available.Count)];
        var actionType = _state.GetCurrentActionType();
        var action = new BpAction
        {
            Side = _state.CurrentSide,
            Type = actionType,
            HeroId = randomHero.HeroId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _state.ApplyAction(action);
        _ = _mqtt.SendActionAsync(action);

        _isLocalTurn = _state.CurrentSide == _localSide;
        UpdateUI();
        UpdateHeroStates();

        if (!_state.IsFinished)
        {
            if (_isLocalTurn)
                StartTimer();
            else
                StatusBar.Text = "等待对方操作";
        }
        else
        {
            ExportBtn.IsVisible = true;
            ReplayBtn.IsVisible = true;
            StatusBar.Text = "对局结束";
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        TimerText.Text = "--";
        TimerBorder.BorderBrush = new SolidColorBrush(Color.Parse("#2A3A4A"));
    }

    private async void OnExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider == null) return;

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出对局记录",
                SuggestedFileName = $"bp_record_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON文件") { Patterns = new[] { "*.json" } } }
            });

            if (file != null)
            {
                var snapshot = _state.GetSnapshot();
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, json);
                StatusBar.Text = "对局记录已导出";
            }
        }
        catch
        {
            StatusBar.Text = "导出失败";
        }
    }

    private void OnReplayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var replayWindow = new ReplayWindow(_state.ActionHistory, _heroes, _config);
        replayWindow.Show();
    }
}
