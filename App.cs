using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("TwitchProxyBot")]
[assembly: AssemblyProduct("TwitchProxyBot")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TwitchProxyBot
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.ToString(), "TwitchProxyBot", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            app.Run(new Shell().Window);
        }
    }

    public class BotRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public string Name { get; set; }
        public string Country { get; set; }
        public string Proxy { get; set; }
        private string ping;
        public string Ping
        {
            get { return ping; }
            set
            {
                ping = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Ping"));
            }
        }
    }

    public class HistoryRow
    {
        public string Channel { get; set; }
        public string Detail { get; set; }
        public string Count { get; set; }
        public string Duration { get; set; }
        public string Name { get; set; }
        public int Kind { get; set; }
        public int Delivered { get; set; }
        public DateTime At { get; set; }
        public TimeSpan Span { get; set; }
    }

    public class Shell
    {
        public readonly Window Window;

        // Wszystko ponizej jest losowane lokalnie. Adresy pochodza wylacznie z zakresow
        // dokumentacyjnych RFC 5737, a aplikacja nie wykonuje zadnych polaczen sieciowych.
        private static readonly string[] Subnets = { "192.0.2", "198.51.100", "203.0.113" };
        private static readonly string[] Countries = { "PL", "DE", "NL", "FR", "SE", "US", "CA", "ES", "CZ", "FI" };
        private static readonly string[] NameParts = { "pixel", "nova", "kruk", "luna", "zen", "frost", "echo", "mati", "ola", "wilk", "neon", "cichy", "orbit", "tomek", "bajt", "lis" };
        private static readonly string[] KindKeys = { "Viewers", "Followers", "Chat" };
        private static readonly string[] ChartKeys = { "ChartViewers", "ChartFollowers", "ChartChat" };
        private const int ChartSlots = 60;
        private const int MaxRows = 200;
        private const double PrepSeconds = 4.0;

        private readonly Random rnd = new Random();
        private readonly ObservableCollection<string> log = new ObservableCollection<string>();
        private readonly ObservableCollection<BotRow> bots = new ObservableCollection<BotRow>();
        private readonly ObservableCollection<HistoryRow> history = new ObservableCollection<HistoryRow>();
        private readonly List<double> series = new List<double>();
        private readonly DispatcherTimer timer = new DispatcherTimer();

        private bool active;
        private string channel;
        private int kind, target, baseViewers;
        private bool rotate;
        private double elapsed, deliverSeconds, delivered, followers, sinceSample, nextLog, sincePing, shownValue;
        private DateTime startedAt;
        private double? hoverX;
        private int themeMode, backdrop = 2;
        private bool initialized, maxHover;
        private IntPtr hwnd;
        private string lang;

        private readonly Grid root;
        private readonly FrameworkElement navPane;
        private readonly Button maxBtn, startBtn, stopBtn;
        private readonly Canvas chart;
        private readonly TextBox channelBox, botSearch;
        private readonly ComboBox kindBox;
        private readonly Slider countSlider;
        private readonly CheckBox gradualToggle, rotateToggle;
        private readonly ProgressBar progress;
        private readonly ScrollViewer logScroll;
        private readonly FrameworkElement[] pages;
        private readonly RadioButton[] navs;
        private readonly Ellipse statusDot;

        public Shell()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml"))
                Window = (Window)XamlReader.Load(stream);

            root = El<Grid>("Root");
            navPane = El<FrameworkElement>("NavPane");
            maxBtn = El<Button>("MaxBtn");
            startBtn = El<Button>("StartBtn");
            stopBtn = El<Button>("StopBtn");
            chart = El<Canvas>("ChartCanvas");
            channelBox = El<TextBox>("ChannelBox");
            botSearch = El<TextBox>("BotSearch");
            kindBox = El<ComboBox>("KindBox");
            countSlider = El<Slider>("CountSlider");
            gradualToggle = El<CheckBox>("GradualToggle");
            rotateToggle = El<CheckBox>("RotateToggle");
            progress = El<ProgressBar>("Progress");
            logScroll = El<ScrollViewer>("LogScroll");
            statusDot = El<Ellipse>("StatusDot");

            pages = new[] { El<FrameworkElement>("PageHome"), El<FrameworkElement>("PageBots"), El<FrameworkElement>("PageHistory"), El<FrameworkElement>("PageSettings"), El<FrameworkElement>("PageInfo") };
            navs = new[] { El<RadioButton>("NavHome"), El<RadioButton>("NavBots"), El<RadioButton>("NavHistory"), El<RadioButton>("NavSettings"), El<RadioButton>("NavInfo") };
            for (int i = 0; i < navs.Length; i++)
            {
                int index = i;
                navs[i].Checked += (s, e) => ShowPage(index);
            }

            El<ItemsControl>("LogList").ItemsSource = log;
            El<ItemsControl>("BotList").ItemsSource = bots;
            El<ItemsControl>("HistoryList").ItemsSource = history;

            WireChrome();
            WireCampaign();
            WireSettings();

            ApplyTheme(IsSystemDark());
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += (s, e) => Tick(0.1);
            ApplyLanguage();
            initialized = true;
        }

        private T El<T>(string name) where T : class
        {
            return (T)Window.FindName(name);
        }

        private void SetText(string name, string value)
        {
            El<TextBlock>(name).Text = value;
        }

        // ---------- Okno ----------

        private void WireChrome()
        {
            El<Button>("MinBtn").Click += (s, e) => Window.WindowState = WindowState.Minimized;
            maxBtn.Click += (s, e) => ToggleMaximize();
            El<Button>("CloseBtn").Click += (s, e) => Window.Close();
            Window.StateChanged += (s, e) =>
            {
                bool max = Window.WindowState == WindowState.Maximized;
                root.Margin = new Thickness(max ? 8 : 0);
                maxBtn.Content = max ? "" : "";
            };
            Window.SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(Window).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
                var options = new Native.WTA_OPTIONS { Flags = 7, Mask = 7 };
                Native.SetWindowThemeAttribute(hwnd, 1, ref options, Marshal.SizeOf(typeof(Native.WTA_OPTIONS)));
                // Bez WS_SYSMENU i koloru belki DWM nie rysuje swoich przyciskow ani czarnego paska pod naszymi.
                Native.SetWindowLong(hwnd, -16, Native.GetWindowLong(hwnd, -16) & ~0x00080000);
                SetDwm(35, -2);
                SetDwm(33, 2); // zaokraglone rogi
                ApplyTheme(CurrentDark());
                ApplyBackdrop();
            };
            Window.Loaded += (s, e) =>
            {
                // Loaded moze przyjsc jeszcze przed SourceInitialized, gdy WindowChrome wymusi pierwszy render.
                HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle).AddHook(WndProc);
                DrawChart();
            };

            double openWidth = 240;
            bool navOpen = true;
            El<Button>("NavToggle").Click += (s, e) =>
            {
                navOpen = !navOpen;
                var anim = new DoubleAnimation(navOpen ? openWidth : 52, TimeSpan.FromMilliseconds(180));
                anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                navPane.BeginAnimation(FrameworkElement.WidthProperty, anim);
            };
        }

        private void ToggleMaximize()
        {
            Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // Zwrocenie HTMAXBUTTON wlacza menu Snap Layouts po najechaniu na przycisk maksymalizacji.
        private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int HTMAXBUTTON = 9;
            switch (msg)
            {
                case 0x0084: // WM_NCHITTEST
                    int raw = lParam.ToInt32();
                    var pt = maxBtn.PointFromScreen(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
                    if (pt.X >= 0 && pt.Y >= 0 && pt.X < maxBtn.ActualWidth && pt.Y < maxBtn.ActualHeight)
                    {
                        SetMaxHover(true);
                        handled = true;
                        return new IntPtr(HTMAXBUTTON);
                    }
                    SetMaxHover(false);
                    break;
                case 0x00A1: // WM_NCLBUTTONDOWN
                    if (wParam.ToInt32() == HTMAXBUTTON) handled = true;
                    break;
                case 0x00A2: // WM_NCLBUTTONUP
                    if (wParam.ToInt32() == HTMAXBUTTON)
                    {
                        handled = true;
                        ToggleMaximize();
                    }
                    break;
                case 0x02A2: // WM_NCMOUSELEAVE
                    SetMaxHover(false);
                    break;
                case 0x001A: // WM_SETTINGCHANGE
                    if (themeMode == 0) ApplyTheme(IsSystemDark());
                    break;
            }
            return IntPtr.Zero;
        }

        private void SetMaxHover(bool hover)
        {
            if (hover == maxHover) return;
            maxHover = hover;
            if (hover) maxBtn.SetResourceReference(Control.BackgroundProperty, "SubtleHover");
            else maxBtn.Background = Brushes.Transparent;
        }

        private void SetDwm(int attribute, int value)
        {
            if (hwnd != IntPtr.Zero) Native.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }

        private void ApplyBackdrop()
        {
            SetDwm(38, backdrop);
        }

        private static bool IsSystemDark()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value is int && (int)value == 0;
            }
        }

        private bool CurrentDark()
        {
            return themeMode == 0 ? IsSystemDark() : themeMode == 2;
        }

        private void ApplyTheme(bool dark)
        {
            var palette = dark ? DarkPalette : LightPalette;
            foreach (var pair in palette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
                brush.Freeze();
                Window.Resources[pair.Key] = brush;
            }
            SetDwm(20, dark ? 1 : 0);
            if (initialized) DrawChart();
        }

        private static readonly Dictionary<string, string> DarkPalette = new Dictionary<string, string>
        {
            { "TextPrimary", "#FFFFFF" }, { "TextSecondary", "#C8FFFFFF" }, { "TextTertiary", "#8BFFFFFF" },
            { "LayerFill", "#4C3A3A3A" }, { "CardBg", "#0DFFFFFF" }, { "CardStroke", "#1A000000" },
            { "ControlFill", "#0FFFFFFF" }, { "ControlHover", "#15FFFFFF" }, { "ControlPressed", "#08FFFFFF" }, { "ControlStroke", "#14FFFFFF" },
            { "SubtleHover", "#0FFFFFFF" }, { "SubtlePressed", "#0AFFFFFF" },
            { "InputFill", "#0FFFFFFF" }, { "InputFocus", "#B31E1E1E" }, { "PopupFill", "#2C2C2C" }, { "ThumbFill", "#454545" }, { "TrackFill", "#8BFFFFFF" },
            { "AccentFill", "#BF94FF" }, { "AccentHover", "#AD78FF" }, { "AccentPressed", "#9A5CFF" }, { "AccentText", "#BF94FF" }, { "OnAccent", "#000000" },
            { "Divider", "#15FFFFFF" }, { "Success", "#6CCB5F" }, { "SuccessBg", "#393D1B" }, { "Critical", "#FF99A4" }
        };

        private static readonly Dictionary<string, string> LightPalette = new Dictionary<string, string>
        {
            { "TextPrimary", "#E4000000" }, { "TextSecondary", "#A0000000" }, { "TextTertiary", "#72000000" },
            { "LayerFill", "#80FFFFFF" }, { "CardBg", "#B3FFFFFF" }, { "CardStroke", "#0F000000" },
            { "ControlFill", "#B3FFFFFF" }, { "ControlHover", "#80F9F9F9" }, { "ControlPressed", "#4DF9F9F9" }, { "ControlStroke", "#17000000" },
            { "SubtleHover", "#09000000" }, { "SubtlePressed", "#06000000" },
            { "InputFill", "#B3FFFFFF" }, { "InputFocus", "#FFFFFF" }, { "PopupFill", "#F9F9F9" }, { "ThumbFill", "#FFFFFF" }, { "TrackFill", "#72000000" },
            { "AccentFill", "#6F24DE" }, { "AccentHover", "#7F3DE6" }, { "AccentPressed", "#8F55EE" }, { "AccentText", "#6F24DE" }, { "OnAccent", "#FFFFFF" },
            { "Divider", "#14000000" }, { "Success", "#0F7B0F" }, { "SuccessBg", "#DFF6DD" }, { "Critical", "#C42B1C" }
        };

        // ---------- Nawigacja ----------

        private void ShowPage(int index)
        {
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

            var page = pages[index];
            var move = new TranslateTransform(0, 14);
            page.RenderTransform = move;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            if (index == 0) DrawChart();
        }

        // ---------- Kampania ----------

        private void WireCampaign()
        {
            countSlider.ValueChanged += (s, e) => CountLabelUpdate();
            channelBox.TextChanged += (s, e) => El<FrameworkElement>("ChannelError").Visibility = Visibility.Collapsed;
            startBtn.Click += (s, e) => Start();
            stopBtn.Click += (s, e) => Stop();

            chart.SizeChanged += (s, e) => DrawChart();
            chart.MouseMove += (s, e) => { hoverX = e.GetPosition(chart).X; DrawChart(); };
            chart.MouseLeave += (s, e) => { hoverX = null; DrawChart(); };

            var view = CollectionViewSource.GetDefaultView(bots);
            botSearch.TextChanged += (s, e) =>
            {
                string query = botSearch.Text.Trim();
                view.Filter = query.Length == 0 ? (Predicate<object>)null : o => ((BotRow)o).Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                UpdateBotEmpty();
            };
            bots.CollectionChanged += (s, e) => UpdateBotEmpty();

            El<Button>("HistoryClear").Click += (s, e) => { history.Clear(); UpdateHistoryEmpty(); };
            UpdateHistoryEmpty();
        }

        private void UpdateBotEmpty()
        {
            El<FrameworkElement>("BotEmpty").Visibility = CollectionViewSource.GetDefaultView(bots).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateHistoryEmpty()
        {
            bool empty = history.Count == 0;
            El<FrameworkElement>("HistoryEmpty").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            El<UIElement>("HistoryClear").IsEnabled = !empty;
        }

        private void Start()
        {
            string name = channelBox.Text.Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,25}$"))
            {
                El<FrameworkElement>("ChannelError").Visibility = Visibility.Visible;
                channelBox.Focus();
                return;
            }

            channel = name;
            kind = kindBox.SelectedIndex;
            target = (int)countSlider.Value;
            rotate = rotateToggle.IsChecked == true;
            deliverSeconds = (6 + Math.Log10(target) * 3) * (gradualToggle.IsChecked == true ? 1.6 : 1.0);
            baseViewers = rnd.Next(4, 60);
            elapsed = delivered = followers = sinceSample = nextLog = sincePing = 0;
            shownValue = 0;
            active = false;
            startedAt = DateTime.Now;

            series.Clear();
            log.Clear();
            bots.Clear();
            El<UIElement>("CampaignInputs").IsEnabled = false;
            El<FrameworkElement>("InfoBar").Visibility = Visibility.Collapsed;
            El<FrameworkElement>("ChartEmpty").Visibility = Visibility.Collapsed;
            startBtn.Visibility = Visibility.Collapsed;
            stopBtn.Visibility = Visibility.Visible;
            SetText("ChartTitle", L(ChartKeys[kind]));
            SetStatus(L("Starting"), "#E5A000", false);

            Log(L("LogNew", channel, L(KindKeys[kind]).ToLower(), target.ToString("N0")));
            timer.Start();
        }

        private void Stop()
        {
            timer.Stop();
            var span = DateTime.Now - startedAt;
            history.Insert(0, MakeHistory("twitch.tv/" + channel, kind, (int)delivered, startedAt, span));
            UpdateHistoryEmpty();

            Log(L("LogStopped"));
            delivered = followers = 0;
            bots.Clear();
            active = false;
            El<UIElement>("CampaignInputs").IsEnabled = true;
            El<FrameworkElement>("InfoBar").Visibility = Visibility.Collapsed;
            startBtn.Visibility = Visibility.Visible;
            stopBtn.Visibility = Visibility.Collapsed;
            progress.Value = 0;
            SetText("ProgressPct", "0%");
            SetText("PhaseText", L("PhaseWaiting"));
            SetText("StatBots", "0");
            SetText("StatViewers", "0");
            SetText("StatFollowers", "0");
            SetText("StatProxy", "0");
            SetText("BotSummary", L("NoActiveBots"));
            SetStatus(L("Idle"), null, false);
        }

        private void SetStatus(string text, string color, bool pulse)
        {
            SetText("StatusText", text);
            if (color == null) statusDot.SetResourceReference(Shape.FillProperty, "TextTertiary");
            else statusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

            if (pulse)
            {
                var anim = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                statusDot.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            else statusDot.BeginAnimation(UIElement.OpacityProperty, null);
        }

        private void Tick(double dt)
        {
            elapsed += dt;
            double overall;

            if (elapsed < PrepSeconds)
            {
                overall = elapsed / PrepSeconds * 0.15;
                SetText("PhaseText", L(elapsed < 1.6 ? "PhaseConnecting" : "PhaseWarming"));
            }
            else if (!active)
            {
                double frac = Math.Min(1, (elapsed - PrepSeconds) / deliverSeconds);
                double eased = 1 - Math.Pow(1 - frac, 2);
                delivered = target * eased;
                overall = 0.15 + 0.85 * frac;
                SetText("PhaseText", L("PhaseDelivering"));
                SetStatus(L("Delivering"), "#E5A000", false);
                if (frac >= 1) Activate();
            }
            else overall = 1;

            progress.Value = overall * 100;
            SetText("ProgressPct", (int)(overall * 100) + "%");

            double wobble = active ? 1 - rnd.NextDouble() * 0.015 : 1;
            int activeBots = (int)(delivered * wobble);
            double viewers = baseViewers + (kind == 1 ? 0 : activeBots);
            if (kind == 1) followers = delivered;
            else followers += delivered > 0 ? rnd.NextDouble() * 0.06 : 0;
            int proxies = delivered <= 0 ? (elapsed > 1 ? 12 : 0) : rotate ? 12 + (int)(delivered / 7) : 12;

            SetText("StatBots", activeBots.ToString("N0"));
            SetText("StatViewers", ((int)viewers).ToString("N0"));
            SetText("StatFollowers", ((int)followers).ToString("N0"));
            SetText("StatProxy", proxies.ToString("N0"));

            shownValue = kind == 0 ? viewers : kind == 1 ? followers : activeBots;
            SetText("ChartValue", ((int)shownValue).ToString("N0"));

            GrowRows();
            WriteLogs(dt);

            sincePing += dt;
            if (sincePing >= 1)
            {
                sincePing = 0;
                foreach (var row in bots) row.Ping = rnd.Next(18, 140) + " ms";
            }

            sinceSample += dt;
            if (sinceSample >= 1)
            {
                sinceSample = 0;
                series.Add(shownValue);
                if (series.Count > ChartSlots) series.RemoveAt(0);
                DrawChart();
            }
        }

        private void Activate()
        {
            active = true;
            ShowActive();
            Log(L("LogDelivered", target.ToString("N0")));
        }

        private void GrowRows()
        {
            int wanted = Math.Min(MaxRows, (int)delivered);
            while (bots.Count < wanted) bots.Add(NewBot());
            if (delivered > 0)
            {
                string summary = L("BotSummary", ((int)delivered).ToString("N0"));
                if (delivered > MaxRows) summary += L("BotSummaryCut", MaxRows);
                SetText("BotSummary", summary);
            }
        }

        private BotRow NewBot()
        {
            return new BotRow
            {
                Name = RandomBotName(),
                Country = Countries[rnd.Next(Countries.Length)],
                Proxy = RandomProxy(),
                Ping = rnd.Next(18, 140) + " ms"
            };
        }

        private string RandomBotName()
        {
            return NameParts[rnd.Next(NameParts.Length)] + "_" + NameParts[rnd.Next(NameParts.Length)] + rnd.Next(10, 999);
        }

        private string RandomProxy()
        {
            return Subnets[rnd.Next(Subnets.Length)] + "." + rnd.Next(1, 255) + ":" + rnd.Next(1080, 9999);
        }

        private void WriteLogs(double dt)
        {
            nextLog -= dt;
            if (nextLog > 0) return;
            nextLog = 0.25 + rnd.NextDouble() * (active ? 1.6 : 0.5);

            if (elapsed < PrepSeconds)
                Log(rnd.Next(2) == 0 ? L("LogProxy", RandomProxy()) : L("LogWarming", rnd.Next(1000, 9999)));
            else if (!active)
                Log(L("LogJoined", RandomBotName(), RandomProxy(), Countries[rnd.Next(Countries.Length)]));
            else if (rotate && rnd.Next(2) == 0)
                Log(L("LogRotation", RandomProxy(), RandomProxy()));
            else
                Log(L("LogHeartbeat", ((int)delivered).ToString("N0")));
        }

        private void Log(string line)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (log.Count > 150) log.RemoveAt(0);
            logScroll.ScrollToEnd();
        }

        // ---------- Wykres ----------

        private static double NiceMax(double value)
        {
            if (value < 10) return 10;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double step in new[] { 1, 2, 2.5, 5, 10 })
                if (step * mag >= value) return step * mag;
            return 10 * mag;
        }

        private TextBlock ChartLabel(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 11, IsHitTestVisible = false };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            return label;
        }

        private void Place(UIElement el, double x, double y)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            chart.Children.Add(el);
        }

        private void DrawChart()
        {
            chart.Children.Clear();
            double w = chart.ActualWidth, h = chart.ActualHeight;
            if (w < 40 || h < 40 || !chart.IsVisible) return;

            const double left = 46, right = 10, top = 8, bottom = 22;
            double plotW = w - left - right, plotH = h - top - bottom;
            double peak = 0;
            foreach (double v in series) peak = Math.Max(peak, v);
            double max = NiceMax(peak * 1.1);

            Func<int, double> xAt = i => left + plotW * (ChartSlots - series.Count + i) / (ChartSlots - 1);
            Func<double, double> yAt = v => top + plotH * (1 - v / max);

            foreach (double frac in new[] { 0.0, 0.5, 1.0 })
            {
                double y = yAt(max * frac);
                var grid = new Line { X1 = left, X2 = w - right, Y1 = y, Y2 = y, StrokeThickness = 1, IsHitTestVisible = false };
                grid.SetResourceReference(Shape.StrokeProperty, "Divider");
                chart.Children.Add(grid);
                var label = ChartLabel((max * frac).ToString("N0"));
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Place(label, left - 8 - label.DesiredSize.Width, y - label.DesiredSize.Height / 2);
            }
            Place(ChartLabel("−60 s"), left, h - 16);
            var now = ChartLabel(L("Now"));
            now.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(now, w - right - now.DesiredSize.Width, h - 16);

            if (series.Count == 0) return;

            var line = new PointCollection();
            var area = new PointCollection();
            area.Add(new Point(xAt(0), yAt(0)));
            for (int i = 0; i < series.Count; i++)
            {
                var p = new Point(xAt(i), yAt(series[i]));
                line.Add(p);
                area.Add(p);
            }
            area.Add(new Point(xAt(series.Count - 1), yAt(0)));

            var fill = new Polygon { Points = area, Opacity = 0.14, IsHitTestVisible = false };
            fill.SetResourceReference(Shape.FillProperty, "AccentFill");
            chart.Children.Add(fill);

            var stroke = new Polyline { Points = line, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            stroke.SetResourceReference(Shape.StrokeProperty, "AccentFill");
            chart.Children.Add(stroke);

            int marked = series.Count - 1;
            if (hoverX.HasValue)
            {
                double slot = (hoverX.Value - left) / plotW * (ChartSlots - 1) - (ChartSlots - series.Count);
                marked = Math.Max(0, Math.Min(series.Count - 1, (int)Math.Round(slot)));
                double cx = xAt(marked);
                var cross = new Line { X1 = cx, X2 = cx, Y1 = top, Y2 = top + plotH, StrokeThickness = 1, IsHitTestVisible = false };
                cross.SetResourceReference(Shape.StrokeProperty, "TextTertiary");
                chart.Children.Add(cross);
            }

            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 2, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, "AccentFill");
            dot.SetResourceReference(Shape.StrokeProperty, "ThumbFill");
            Place(dot, xAt(marked) - 5, yAt(series[marked]) - 5);

            if (hoverX.HasValue) DrawTooltip(marked, xAt(marked), yAt(series[marked]), w);
        }

        private void DrawTooltip(int index, double x, double y, double width)
        {
            int ago = series.Count - 1 - index;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = ((int)series[index]).ToString("N0"), FontWeight = FontWeights.SemiBold });
            var when = new TextBlock { Text = ago == 0 ? L("Now") : L("SecondsAgo", ago), FontSize = 11 };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(when);

            var tip = new Border { Child = panel, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            tip.SetResourceReference(Border.BackgroundProperty, "PopupFill");
            tip.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double tx = x + 12;
            if (tx + tip.DesiredSize.Width > width) tx = x - 12 - tip.DesiredSize.Width;
            Place(tip, tx, Math.Max(0, y - tip.DesiredSize.Height - 6));
        }

        // ---------- Jezyk ----------

        private string L(string key, params object[] args)
        {
            string text = Strings[key][lang == "pl" ? 0 : 1];
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private static string LoadLanguage()
        {
            using (var reg = Registry.CurrentUser.OpenSubKey(RegistryPath))
            {
                var saved = reg == null ? null : reg.GetValue("Language") as string;
                if (saved == "pl" || saved == "en") return saved;
            }
            return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";
        }

        private void ApplyLanguage()
        {
            var culture = new CultureInfo(lang == "pl" ? "pl-PL" : "en-US");
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            foreach (var pair in Strings)
                Window.Resources["S_" + pair.Key] = pair.Value[lang == "pl" ? 0 : 1];

            // Zamkniete listy pokazuja kopie tekstu, wiec trzeba je wybrac od nowa.
            foreach (var name in new[] { "KindBox", "ThemeBox", "RegionBox" })
            {
                var box = El<ComboBox>(name);
                int selected = box.SelectedIndex;
                box.SelectedIndex = -1;
                box.SelectedIndex = selected;
            }

            for (int i = 0; i < history.Count; i++)
            {
                var row = history[i];
                history[i] = MakeHistory(row.Name, row.Kind, row.Delivered, row.At, row.Span);
            }

            SetText("ChartTitle", L(ChartKeys[kind]));
            if (!timer.IsEnabled)
            {
                SetText("PhaseText", L("PhaseWaiting"));
                SetText("BotSummary", L("NoActiveBots"));
                SetStatus(L("Idle"), null, false);
            }
            else if (active) ShowActive();
            else SetStatus(L("Starting"), "#E5A000", false);

            CountLabelUpdate();
            DrawChart();
        }

        private void CountLabelUpdate()
        {
            SetText("CountLabel", ((int)countSlider.Value).ToString("N0"));
        }

        private void ShowActive()
        {
            SetText("PhaseText", L("PhaseActive"));
            SetStatus(L("Active", channel), "#E5484D", true);
            SetText("InfoBarText", L("InfoDelivered", target.ToString("N0"), channel));
            El<FrameworkElement>("InfoBar").Visibility = Visibility.Visible;
        }

        private HistoryRow MakeHistory(string name, int kindIndex, int count, DateTime at, TimeSpan span)
        {
            return new HistoryRow
            {
                Name = name, Kind = kindIndex, Delivered = count, At = at, Span = span,
                Channel = name,
                Detail = L(KindKeys[kindIndex]) + " · " + at.ToString("dd.MM.yyyy HH:mm"),
                Count = L("HistoryCount", count.ToString("N0")),
                Duration = (int)span.TotalMinutes + " min " + span.Seconds + " s"
            };
        }

        private const string RegistryPath = @"Software\TwitchProxyBot";

        private sealed class StringTable : Dictionary<string, string[]>
        {
            public void Add(string key, string pl, string en) { Add(key, new[] { pl, en }); }
        }

        private static readonly StringTable Strings = new StringTable
        {
            { "On", "Wł.", "On" }, { "Off", "Wył.", "Off" },
            { "Minimize", "Minimalizuj", "Minimize" }, { "Maximize", "Maksymalizuj", "Maximize" }, { "Close", "Zamknij", "Close" }, { "CollapseNav", "Zwiń panel", "Collapse panel" },
            { "Home", "Panel główny", "Dashboard" }, { "Bots", "Boty", "Bots" }, { "History", "Historia", "History" }, { "Settings", "Ustawienia", "Settings" }, { "Info", "Informacje", "About" },
            { "Idle", "Bezczynny", "Idle" }, { "Starting", "Uruchamianie", "Starting" }, { "Delivering", "Dostarczanie", "Delivering" }, { "Active", "Aktywna · {0}", "Active · {0}" },
            { "ActiveBots", "Aktywne boty", "Active bots" }, { "Viewers", "Widzowie", "Viewers" }, { "NewFollowers", "Nowi followersi", "New followers" }, { "ProxyOnline", "Proxy online", "Proxies online" },
            { "NewCampaign", "Nowa kampania", "New campaign" }, { "Channel", "Kanał Twitch", "Twitch channel" }, { "ChannelError", "Podaj nazwę kanału (3–25 znaków: litery, cyfry, _).", "Enter a channel name (3–25 characters: letters, digits, _)." },
            { "BotKind", "Rodzaj botów", "Bot type" }, { "Followers", "Followersi", "Followers" }, { "Chat", "Czat", "Chat" }, { "BotCount", "Liczba botów", "Number of bots" },
            { "Gradual", "Stopniowe dodawanie", "Gradual delivery" }, { "Rotation", "Rotacja proxy", "Proxy rotation" }, { "Start", "Uruchom", "Start" }, { "Stop", "Zatrzymaj", "Stop" },
            { "ChartViewers", "Widzowie na kanale", "Channel viewers" }, { "ChartFollowers", "Nowi followersi", "New followers" }, { "ChartChat", "Aktywni na czacie", "Active in chat" },
            { "Last60", "Ostatnie 60 sekund", "Last 60 seconds" }, { "ChartEmpty", "Uruchom kampanię, aby zobaczyć dane", "Start a campaign to see data" }, { "Now", "teraz", "now" }, { "SecondsAgo", "{0} s temu", "{0} s ago" },
            { "PhaseWaiting", "Oczekiwanie na uruchomienie", "Waiting to start" }, { "PhaseConnecting", "Łączenie z pulą proxy…", "Connecting to proxy pool…" }, { "PhaseWarming", "Rozgrzewanie sesji…", "Warming up sessions…" }, { "PhaseDelivering", "Dostarczanie botów…", "Delivering bots…" }, { "PhaseActive", "Kampania aktywna", "Campaign active" },
            { "InfoDelivered", "Dostarczono {0} botów na kanał {1}.", "Delivered {0} bots to channel {1}." }, { "Log", "Dziennik", "Log" },
            { "LogNew", "Nowa kampania: twitch.tv/{0}, {1}, botów: {2}", "New campaign: twitch.tv/{0}, {1}, bots: {2}" }, { "LogStopped", "Kampania zatrzymana, sesje zamknięte", "Campaign stopped, sessions closed" }, { "LogDelivered", "Dostarczanie zakończone: {0}/{0}", "Delivery finished: {0}/{0}" },
            { "LogProxy", "Proxy {0} odpowiada", "Proxy {0} responding" }, { "LogWarming", "Rozgrzewanie sesji {0}", "Warming up session {0}" }, { "LogJoined", "{0} dołączył przez {1} [{2}]", "{0} joined via {1} [{2}]" }, { "LogRotation", "Rotacja proxy: {0} → {1}", "Proxy rotation: {0} → {1}" }, { "LogHeartbeat", "Heartbeat OK, sesje: {0}", "Heartbeat OK, sessions: {0}" },
            { "SearchPrefix", "Szukaj: ", "Search: " }, { "SearchBot", "Szukaj bota", "Search bots" }, { "NoActiveBots", "Brak aktywnych botów", "No active bots" }, { "BotSummary", "Aktywne boty: {0}", "Active bots: {0}" }, { "BotSummaryCut", ", pokazano pierwsze {0}", ", showing the first {0}" },
            { "Name", "Nazwa", "Name" }, { "Country", "Kraj", "Country" }, { "Status", "Status", "Status" }, { "BotActive", "Aktywny", "Active" }, { "NoBots", "Brak botów do wyświetlenia", "No bots to show" },
            { "Clear", "Wyczyść", "Clear" }, { "HistoryEmpty", "Zakończone kampanie pojawią się tutaj", "Finished campaigns will appear here" }, { "HistoryCount", "Boty: {0}", "Bots: {0}" },
            { "Appearance", "Wygląd", "Appearance" }, { "Language", "Język", "Language" }, { "LanguageDesc", "Język interfejsu aplikacji", "App interface language" },
            { "Theme", "Motyw", "Theme" }, { "ThemeDesc", "Wybierz jasny, ciemny lub zgodny z systemem", "Choose light, dark or match the system" }, { "ThemeSystem", "Systemowy", "System" }, { "ThemeLight", "Jasny", "Light" }, { "ThemeDark", "Ciemny", "Dark" },
            { "Backdrop", "Materiał tła", "Backdrop material" }, { "BackdropDesc", "Efekt tła okna Windows 11", "Windows 11 window backdrop effect" },
            { "Network", "Sieć", "Network" }, { "Region", "Region proxy", "Proxy region" }, { "RegionDesc", "Lokalizacja puli połączeń", "Connection pool location" }, { "RegionAuto", "Automatycznie", "Automatic" }, { "RegionEurope", "Europa", "Europe" }, { "RegionNA", "Ameryka Północna", "North America" }, { "RegionAsia", "Azja", "Asia" },
            { "Encryption", "Szyfrowanie sesji", "Session encryption" }, { "EncryptionDesc", "Tuneluj ruch botów", "Tunnel bot traffic" }, { "Aggressive", "Tryb agresywny", "Aggressive mode" }, { "AggressiveDesc", "Szybsze dostarczanie", "Faster delivery" },
            { "AppVersion", "Wersja aplikacji", "App version" }, { "SimTitle", "To jest symulacja", "This is a simulation" },
            { "SimText", "Aplikacja nie łączy się z Twitchem ani z żadnym innym serwisem. Nie dodaje botów, a wszystkie liczby, nazwy i adresy są losowane lokalnie.", "The app doesn't connect to Twitch or any other service. It doesn't add bots, and all numbers, names and addresses are randomly generated on your machine." }
        };

        // ---------- Ustawienia ----------

        private void WireSettings()
        {
            var languageBox = El<ComboBox>("LanguageBox");
            lang = LoadLanguage();
            languageBox.SelectedIndex = lang == "pl" ? 0 : 1;
            languageBox.SelectionChanged += (s, e) =>
            {
                lang = languageBox.SelectedIndex == 0 ? "pl" : "en";
                using (var reg = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    reg.SetValue("Language", lang);
                ApplyLanguage();
            };

            El<ComboBox>("ThemeBox").SelectionChanged += (s, e) =>
            {
                if (El<ComboBox>("ThemeBox").SelectedIndex < 0) return;
                themeMode = El<ComboBox>("ThemeBox").SelectedIndex;
                if (initialized) ApplyTheme(CurrentDark());
            };
            El<ComboBox>("BackdropBox").SelectionChanged += (s, e) =>
            {
                backdrop = new[] { 2, 4, 3 }[El<ComboBox>("BackdropBox").SelectedIndex];
                if (initialized) ApplyBackdrop();
            };
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WTA_OPTIONS { public uint Flags, Mask; }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("uxtheme.dll")]
        public static extern int SetWindowThemeAttribute(IntPtr hwnd, int type, ref WTA_OPTIONS options, int size);
    }
}
