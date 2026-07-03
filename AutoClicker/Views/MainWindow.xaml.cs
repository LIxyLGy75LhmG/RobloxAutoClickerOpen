using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoClicker.Enums;
using AutoClicker.Models;
using AutoClicker.Utils;
using Serilog;
using CheckBox = System.Windows.Controls.CheckBox;
using MouseAction = AutoClicker.Enums.MouseAction;
using MouseButton = AutoClicker.Enums.MouseButton;
using MouseCursor = System.Windows.Forms.Cursor;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using Point = System.Drawing.Point;
using Timer = System.Timers.Timer;

namespace AutoClicker.Views
{
    public partial class MainWindow : Window
    {
        public AutoClickerSettings AutoClickerSettings
        {
            get { return (AutoClickerSettings)GetValue(CurrentSettingsProperty); }
            set { SetValue(CurrentSettingsProperty, value); }
        }

        public static readonly DependencyProperty CurrentSettingsProperty =
           DependencyProperty.Register(nameof(AutoClickerSettings), typeof(AutoClickerSettings), typeof(MainWindow),
               new UIPropertyMetadata(SettingsUtils.CurrentSettings.AutoClickerSettings));

        // ===== Click engine (dedicated background thread) =====
        private volatile bool _isClicking;
        private System.Threading.Thread _clickThread;
        private int _intervalMs = 100;
        private int _timesToRepeat = -1;      // -1 = infinite
        private int _timesClicked;

        // Click parameters snapshotted at Start so the loop never touches the UI-thread-bound settings.
        private int _clickDownFlag;
        private int _clickUpFlag;
        private int _numActions = 1;
        private LocationMode _locationMode;
        private int _pickedX;
        private int _pickedY;

        private readonly Timer afkTimer;
        private readonly Uri runningIconUri =
            new Uri(Constants.RUNNING_ICON_RESOURCE_PATH, UriKind.Relative);

        private NotifyIcon systemTrayIcon;
        private SystemTrayMenu systemTrayMenu;
        private AboutWindow aboutWindow = null;
        private SettingsWindow settingsWindow = null;
        private CaptureMouseScreenCoordinatesWindow captureMouseCoordinatesWindow;

        private ImageSource _defaultIcon;
        private IntPtr _mainWindowHandle;
        private HwndSource _source;

        #region Life Cycle

        public MainWindow()
        {
            afkTimer = new Timer(30000);
            afkTimer.Elapsed += OnAfkTimerElapsed;

            DataContext = this;
            ResetTitle();
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _mainWindowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_mainWindowHandle);
            _source.AddHook(StartStopHooks);
            _defaultIcon = Icon;

            SettingsUtils.HotkeyChangedEvent += SettingsUtils_HotkeyChangedEvent;
            SettingsUtils_HotkeyChangedEvent(this, new HotkeyChangedEventArgs()
            {
                Hotkey = SettingsUtils.CurrentSettings.HotkeySettings.StartHotkey,
                Operation = Operation.Start
            });
            SettingsUtils_HotkeyChangedEvent(this, new HotkeyChangedEventArgs()
            {
                Hotkey = SettingsUtils.CurrentSettings.HotkeySettings.StopHotkey,
                Operation = Operation.Stop
            });
            SettingsUtils_HotkeyChangedEvent(this, new HotkeyChangedEventArgs()
            {
                Hotkey = SettingsUtils.CurrentSettings.HotkeySettings.ToggleHotkey,
                Operation = Operation.Toggle
            });

            RadioButtonSelectedLocationMode_CurrentLocation.Checked += RadioButtonSelectedLocationMode_CurrentLocationOnChecked;

            InitializeSystemTrayMenu();
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClicking = false;
            _source.RemoveHook(StartStopHooks);

            SettingsUtils.HotkeyChangedEvent -= SettingsUtils_HotkeyChangedEvent;

            foreach (int hotkeyId in Constants.ALL_HOTKEY_IDS)
            {
                DeregisterHotkey(hotkeyId);
            }

            RadioButtonSelectedLocationMode_CurrentLocation.Checked -= RadioButtonSelectedLocationMode_CurrentLocationOnChecked;

            DisposeSystemTrayMenu();

            Log.Information("Application closing");
            Log.Debug("==================================================");
            Log.CloseAndFlush();

            base.OnClosed(e);
        }

        #endregion Life Cycle

        #region Commands

        private void StartCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            StartClicking();
        }

        private void StartCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CanStartOperation();
        }

        private void StopCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            StopClicking();
        }

        private void StopCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _isClicking;
        }

        private void ToggleCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            if (_isClicking)
                StopClicking();
            else
                StartClicking();
        }

        private void ToggleCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = CanStartOperation() | _isClicking;
        }

        private void SaveSettingsCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            Log.Information("Saving Settings");
            SettingsUtils.SetApplicationSettings(AutoClickerSettings);
        }

        private void HotkeySettingsCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow();
                settingsWindow.Closed += (o, args) => settingsWindow = null;
            }

            settingsWindow.Show();
        }

        private void ExitCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            Exit();
        }

        private void Exit()
        {
            _isClicking = false;
            Application.Current.Shutdown();
        }

        private void AboutCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            if (aboutWindow == null)
            {
                aboutWindow = new AboutWindow();
                aboutWindow.Closed += (o, args) => aboutWindow = null;
            }

            aboutWindow.Show();
        }

        private void CaptureMouseScreenCoordinatesCommand_Execute(object sender, ExecutedRoutedEventArgs e)
        {
            if (captureMouseCoordinatesWindow == null)
            {
                captureMouseCoordinatesWindow = new CaptureMouseScreenCoordinatesWindow();
                captureMouseCoordinatesWindow.OnCoordinatesCaptured += CaptureMouseCoordinatesWindow_OnCoordinatesCaptured;
                captureMouseCoordinatesWindow.Closed += (o, args) =>
                {
                    captureMouseCoordinatesWindow.OnCoordinatesCaptured -= CaptureMouseCoordinatesWindow_OnCoordinatesCaptured;
                    captureMouseCoordinatesWindow = null;
                };
            }

            captureMouseCoordinatesWindow.Show();
        }

        private void CaptureMouseCoordinatesWindow_OnCoordinatesCaptured(object sender, Point point)
        {
            TextBoxPickedXValue.Text = point.X.ToString();
            TextBoxPickedYValue.Text = point.Y.ToString();
            RadioButtonSelectedLocationMode_PickedLocation.IsChecked = true;
        }

        #endregion Commands

        #region Click Engine

        private void StartClicking()
        {
            if (_isClicking)
                return;

            SnapshotClickSettings();

            _intervalMs = CalculateInterval();
            if (_intervalMs < 1)
                _intervalMs = 1;

            _timesClicked = 0;
            _isClicking = true;

            _clickThread = new System.Threading.Thread(ClickLoop)
            {
                IsBackground = true,
                Name = "AutoClickerLoop"
            };
            _clickThread.Start();

            Log.Information("Starting operation, interval={Interval}ms, mode={Mode}, repeat={Repeat}",
                _intervalMs, _locationMode, _timesToRepeat);

            Icon = new BitmapImage(runningIconUri);
            Title = Constants.MAIN_WINDOW_TITLE_DEFAULT + Constants.MAIN_WINDOW_TITLE_RUNNING;
            if (systemTrayIcon != null)
                systemTrayIcon.Text = Constants.MAIN_WINDOW_TITLE_DEFAULT + Constants.MAIN_WINDOW_TITLE_RUNNING;

            CommandManager.InvalidateRequerySuggested();
        }

        // The click loop runs on its own background thread: click, then sleep the interval, forever
        // (or until the repeat count is reached). Every click is wrapped so a single failure can never
        // kill the loop, and nothing here touches the UI thread, so the app stays responsive.
        private void ClickLoop()
        {
            try
            {
                while (_isClicking)
                {
                    try
                    {
                        PerformClick();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Click iteration failed");
                    }

                    _timesClicked++;
                    if (_timesToRepeat > 0 && _timesClicked >= _timesToRepeat)
                    {
                        _isClicking = false;
                        Dispatcher.BeginInvoke(new Action(StopClicking));
                        break;
                    }

                    System.Threading.Thread.Sleep(_intervalMs);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Click loop crashed");
                _isClicking = false;
            }
        }

        private void StopClicking()
        {
            _isClicking = false;
            _clickThread = null;

            Log.Information("Stopping operation");
            ResetTitle();
            if (_defaultIcon != null)
                Icon = _defaultIcon;

            CommandManager.InvalidateRequerySuggested();
        }

        // Read the click settings once (on the UI thread that calls Start) into plain fields,
        // so the background loop can run without ever touching UI-thread-bound settings.
        private void SnapshotClickSettings()
        {
            switch (AutoClickerSettings.SelectedMouseButton)
            {
                case MouseButton.Right:
                    _clickDownFlag = Constants.MOUSEEVENTF_RIGHTDOWN;
                    _clickUpFlag = Constants.MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseButton.Middle:
                    _clickDownFlag = Constants.MOUSEEVENTF_MIDDLEDOWN;
                    _clickUpFlag = Constants.MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    _clickDownFlag = Constants.MOUSEEVENTF_LEFTDOWN;
                    _clickUpFlag = Constants.MOUSEEVENTF_LEFTUP;
                    break;
            }

            _numActions = AutoClickerSettings.SelectedMouseAction == MouseAction.Single ? 1 : 2;
            _locationMode = AutoClickerSettings.SelectedLocationMode;
            _pickedX = AutoClickerSettings.PickedXValue;
            _pickedY = AutoClickerSettings.PickedYValue;
            _timesToRepeat = AutoClickerSettings.SelectedRepeatMode == RepeatMode.Count
                ? AutoClickerSettings.SelectedTimesToRepeat
                : -1;
        }

        // A real click: move to target, press, hold briefly, release. The hold is what makes games
        // (Roblox) register it — an instant down+up lands between game frames and is ignored.
        private void PerformClick()
        {
            int x, y;
            if (_locationMode == LocationMode.CurrentLocation)
            {
                Point p = MouseCursor.Position;
                x = p.X;
                y = p.Y;
            }
            else
            {
                x = _pickedX;
                y = _pickedY;
            }

            // Never click our own window — otherwise the clicker can hit its own Stop/Exit and close itself.
            if (Win32ApiUtils.GetRootWindowAt(x, y) == _mainWindowHandle)
                return;

            for (int i = 0; i < _numActions; ++i)
            {
                Win32ApiUtils.SetCursorPosition(x, y);
                Win32ApiUtils.SendMouseFlag((uint)_clickDownFlag);
                System.Threading.Thread.Sleep(Constants.CLICK_HOLD_MS);
                Win32ApiUtils.SendMouseFlag((uint)_clickUpFlag);

                if (_numActions > 1 && i == 0)
                    System.Threading.Thread.Sleep(Constants.DOUBLE_CLICK_GAP_MS);
            }
        }

        #endregion Click Engine

        #region Helper Methods

        private int CalculateInterval()
        {
            return AutoClickerSettings.Milliseconds
                + (AutoClickerSettings.Seconds * 1000)
                + (AutoClickerSettings.Minutes * 60 * 1000)
                + (AutoClickerSettings.Hours * 60 * 60 * 1000);
        }

        private bool IsIntervalValid()
        {
            return CalculateInterval() > 0;
        }

        private bool CanStartOperation()
        {
            return !_isClicking && IsRepeatModeValid() && IsIntervalValid();
        }

        private bool IsRepeatModeValid()
        {
            return AutoClickerSettings.SelectedRepeatMode == RepeatMode.Infinite
                || (AutoClickerSettings.SelectedRepeatMode == RepeatMode.Count && AutoClickerSettings.SelectedTimesToRepeat > 0);
        }

        private void ResetTitle()
        {
            Title = Constants.MAIN_WINDOW_TITLE_DEFAULT;
            if (systemTrayIcon != null)
            {
                systemTrayIcon.Text = Constants.MAIN_WINDOW_TITLE_DEFAULT;
            }
        }

        private void InitializeSystemTrayMenu()
        {
            systemTrayIcon = new NotifyIcon
            {
                Visible = true,
                Icon = AssemblyUtils.GetApplicationIcon(),
                Text = Constants.MAIN_WINDOW_TITLE_DEFAULT
            };
            systemTrayIcon.Click += SystemTrayIcon_Click;

            systemTrayMenu = new SystemTrayMenu();
            systemTrayMenu.SystemTrayMenuActionEvent += SystemTrayMenu_SystemTrayMenuActionEvent;
        }

        private void DisposeSystemTrayMenu()
        {
            systemTrayIcon.Click -= SystemTrayIcon_Click;
            systemTrayIcon.Dispose();

            systemTrayMenu.SystemTrayMenuActionEvent -= SystemTrayMenu_SystemTrayMenuActionEvent;
            systemTrayMenu.Dispose();
        }

        private void ReRegisterHotkey(IEnumerable<int> hotkeyIds, KeyMapping hotkey, bool includeModifiers)
        {
            foreach (int hotkeyId in hotkeyIds)
            {
                DeregisterHotkey(hotkeyId);
            }
            RegisterHotkey(hotkeyIds, hotkey, includeModifiers);
        }

        private void RegisterHotkey(IEnumerable<int> hotkeyIds, KeyMapping hotkey, bool includeModifiers)
        {
            Log.Information("RegisterHotkey with hotkey={Hotkey}, includeModifiers={IncludeModifiers}", hotkey.DisplayName, includeModifiers);
            IEnumerable<(int, int)> hotkeyIdsToModifiers = Enumerable.Zip(hotkeyIds, Constants.MODIFIERS, (first, second) => ValueTuple.Create(first, second));
            if (includeModifiers)
            {
                foreach ((int, int) item in hotkeyIdsToModifiers)
                {
                    Win32ApiUtils.RegisterHotkey(_mainWindowHandle, item.Item1, item.Item2, hotkey.VirtualKeyCode);
                }
            }
            else
            {
                Win32ApiUtils.RegisterHotkey(_mainWindowHandle, hotkeyIdsToModifiers.ElementAt(0).Item1, hotkeyIdsToModifiers.ElementAt(0).Item2, hotkey.VirtualKeyCode);
            }
        }

        private void DeregisterHotkey(int hotkeyId)
        {
            Log.Information("DeregisterHotkey with hotkeyId={HotkeyId}", hotkeyId);
            if (Win32ApiUtils.DeregisterHotkey(_mainWindowHandle, hotkeyId))
                return;
            Log.Debug("No hotkey registered on {HotkeyId}", hotkeyId);
        }

        #endregion Helper Methods

        #region Event Handlers

        private IntPtr StartStopHooks(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Constants.WM_HOTKEY && Constants.ALL_HOTKEY_IDS.Contains(wParam.ToInt32()))
            {
                int virtualKey = ((int)lParam >> 16) & 0xFFFF;
                if (virtualKey == SettingsUtils.CurrentSettings.HotkeySettings.StartHotkey.VirtualKeyCode && CanStartOperation())
                {
                    StartClicking();
                }
                if (virtualKey == SettingsUtils.CurrentSettings.HotkeySettings.StopHotkey.VirtualKeyCode && _isClicking)
                {
                    StopClicking();
                }
                if (virtualKey == SettingsUtils.CurrentSettings.HotkeySettings.ToggleHotkey.VirtualKeyCode && (CanStartOperation() | _isClicking))
                {
                    if (_isClicking)
                        StopClicking();
                    else
                        StartClicking();
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SettingsUtils_HotkeyChangedEvent(object sender, HotkeyChangedEventArgs e)
        {
            Log.Information("HotkeyChangedEvent with operation={Operation}, hotkey={Hotkey}, includeModifiers={IncludeModifiers}", e.Operation, e.Hotkey.DisplayName, e.IncludeModifiers);
            switch (e.Operation)
            {
                case Operation.Start:
                    ReRegisterHotkey(Constants.START_HOTKEY_IDS, e.Hotkey, e.IncludeModifiers);
                    startButton.Content = $"{Constants.MAIN_WINDOW_START_BUTTON_CONTENT} ({e.Hotkey.DisplayName})";
                    break;
                case Operation.Stop:
                    ReRegisterHotkey(Constants.STOP_HOTKEY_IDS, e.Hotkey, e.IncludeModifiers);
                    stopButton.Content = $"{Constants.MAIN_WINDOW_STOP_BUTTON_CONTENT} ({e.Hotkey.DisplayName})";
                    break;
                case Operation.Toggle:
                    ReRegisterHotkey(Constants.TOGGLE_HOTKEY_IDS, e.Hotkey, e.IncludeModifiers);
                    toggleButton.Content = $"{Constants.MAIN_WINDOW_TOGGLE_BUTTON_CONTENT} ({e.Hotkey.DisplayName})";
                    break;
                default:
                    Log.Warning("Operation {Operation} not supported!", e.Operation);
                    throw new NotSupportedException($"Operation {e.Operation} not supported!");
            }
        }

        private void SystemTrayIcon_Click(object sender, EventArgs e)
        {
            systemTrayMenu.IsOpen = true;
            systemTrayMenu.Focus();
        }

        private void SystemTrayMenu_SystemTrayMenuActionEvent(object sender, SystemTrayMenuActionEventArgs e)
        {
            switch (e.Action)
            {
                case SystemTrayMenuAction.Show:
                    Show();
                    break;
                case SystemTrayMenuAction.Hide:
                    Hide();
                    break;
                case SystemTrayMenuAction.Exit:
                    Exit();
                    break;
                default:
                    Log.Warning("Action {Action} not supported!", e.Action);
                    throw new NotSupportedException($"Action {e.Action} not supported!");
            }
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (aboutWindow == null)
            {
                aboutWindow = new AboutWindow();
                aboutWindow.Closed += (o, args) => aboutWindow = null;
            }

            aboutWindow.Show();
        }

        private void MinimizeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            systemTrayMenu.ToggleMenuItemsVisibility(true);
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Exit();
        }

        private void RadioButtonSelectedLocationMode_CurrentLocationOnChecked(object sender, RoutedEventArgs e)
        {
            TextBoxPickedXValue.Text = string.Empty;
            TextBoxPickedYValue.Text = string.Empty;
        }

        private void TopMostCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            CheckBox checkbox = (CheckBox)sender;
            Topmost = checkbox.IsChecked.Value;
        }

        // AFK Anti-Idle: every 30s fires a single held left click at the current cursor position
        // so Roblox (and similar games) won't kick you for being idle.
        private void AfkModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            CheckBox checkbox = (CheckBox)sender;
            if (checkbox.IsChecked.Value)
            {
                afkTimer.Start();
                Log.Information("AFK anti-idle mode enabled");
            }
            else
            {
                afkTimer.Stop();
                Log.Information("AFK anti-idle mode disabled");
            }
        }

        private void OnAfkTimerElapsed(object sender, ElapsedEventArgs e)
        {
            Win32ApiUtils.ExecuteMouseEvent(Constants.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            System.Threading.Thread.Sleep(Constants.CLICK_HOLD_MS);
            Win32ApiUtils.ExecuteMouseEvent(Constants.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        #endregion Event Handlers
    }
}
