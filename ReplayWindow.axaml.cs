using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace BPTrainer;

public partial class ReplayWindow : Window
{
    private List<BpAction> _actions;
    private List<HeroData> _heroes;
    private BpConfig _config;
    private BpState _replayState;
    private int _currentStep = 0;
    private DispatcherTimer? _playTimer;
    private bool _isPlaying = false;
    private double _playbackSpeed = 1.0;

    public ReplayWindow(List<BpAction> actions, List<HeroData> heroes, BpConfig config)
    {
        InitializeComponent();

        _actions = new List<BpAction>(actions);
        _heroes = heroes;
        _config = config;
        _replayState = new BpState(_config);

        SpeedSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _playbackSpeed = SpeedSlider.Value;
                SpeedText.Text = $"{_playbackSpeed:F1}x";
            }
        };

        UpdateReplayUI();
    }

    private void UpdateReplayUI()
    {
        UpdateSlot("ReplayBlueBan", _replayState.BlueBans, 2, true);
        UpdateSlot("ReplayRedBan", _replayState.RedBans, 2, true);
        UpdateSlot("ReplayBluePick", _replayState.BluePicks, 5, false);
        UpdateSlot("ReplayRedPick", _replayState.RedPicks, 5, false);

        StepInfo.Text = $"步骤 {_currentStep}/{_actions.Count}";

        if (_currentStep > 0 && _currentStep <= _actions.Count)
        {
            var action = _actions[_currentStep - 1];
            var hero = _heroes.FirstOrDefault(h => h.HeroId == action.HeroId);

            ActionSideText.Text = action.Side == BpSide.Blue ? "蓝色方" : "红色方";
            ActionSideText.Foreground = action.Side == BpSide.Blue
                ? new SolidColorBrush(Color.Parse("#4A9EFF"))
                : new SolidColorBrush(Color.Parse("#FF4A4A"));

            ActionTypeText.Text = action.Type == BpActionType.Ban ? "BAN" : "PICK";
            ActionHeroText.Text = hero?.HeroName ?? action.HeroId;
        }
        else
        {
            ActionSideText.Text = "";
            ActionTypeText.Text = "";
            ActionHeroText.Text = "";
        }

        UpdateActionLog();
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
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
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
    }

    private void UpdateActionLog()
    {
        ActionLog.Children.Clear();

        for (int i = 0; i < _currentStep && i < _actions.Count; i++)
        {
            var action = _actions[i];
            var hero = _heroes.FirstOrDefault(h => h.HeroId == action.HeroId);
            var sideText = action.Side == BpSide.Blue ? "蓝" : "红";
            var typeText = action.Type == BpActionType.Ban ? "BAN" : "PICK";
            var heroName = hero?.HeroName ?? action.HeroId;

            var textBlock = new TextBlock
            {
                Text = $"{i + 1}. [{sideText}] {typeText}: {heroName}",
                FontSize = 13,
                Foreground = action.Side == BpSide.Blue
                    ? new SolidColorBrush(Color.Parse("#4A9EFF"))
                    : new SolidColorBrush(Color.Parse("#FF4A4A"))
            };

            ActionLog.Children.Add(textBlock);
        }

        if (ActionLog.Children.Count > 0)
        {
            LogScroller.Offset = new Vector(0, double.MaxValue);
        }
    }

    private void OnRestartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopPlayback();
        _currentStep = 0;
        _replayState = new BpState(_config);
        UpdateReplayUI();
    }

    private void OnPrevClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentStep > 0)
        {
            StopPlayback();
            _currentStep--;
            RebuildStateToStep();
            UpdateReplayUI();
        }
    }

    private void OnNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentStep < _actions.Count)
        {
            StopPlayback();
            _currentStep++;
            var action = _actions[_currentStep - 1];
            _replayState.ApplyAction(action);
            UpdateReplayUI();
        }
    }

    private void OnPlayPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (_currentStep >= _actions.Count)
        {
            _currentStep = 0;
            _replayState = new BpState(_config);
            UpdateReplayUI();
        }

        _isPlaying = true;
        PlayPauseBtn.Content = "暂停";

        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0 / _playbackSpeed)
        };
        _playTimer.Tick += PlayTimer_Tick;
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        PlayPauseBtn.Content = "播放";
        _playTimer?.Stop();
        _playTimer = null;
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentStep < _actions.Count)
        {
            _currentStep++;
            var action = _actions[_currentStep - 1];
            _replayState.ApplyAction(action);
            UpdateReplayUI();
        }
        else
        {
            StopPlayback();
        }
    }

    private void RebuildStateToStep()
    {
        _replayState = new BpState(_config);
        for (int i = 0; i < _currentStep && i < _actions.Count; i++)
        {
            _replayState.ApplyAction(_actions[i]);
        }
    }
}
