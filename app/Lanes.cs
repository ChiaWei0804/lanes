// Lanes: the window for btr-local.cjs. Starts the controller hidden, shows its status and settings in a
// frameless rounded WPF window with a notification-area icon, and ends the controller when Lanes exits.
// The controller watches this process's end of its stdin pipe, so it also ends if Lanes crashes.
// Portable: settings.json, lanes.log and node.exe live in the Lanes folder. UI text comes from app/lang/*.json.
// Built by build.cjs with the C# 5 compiler that ships with Windows (.NET Framework 4).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

public static class Program {
  const string Api = "http://127.0.0.1:39230";
  const string Title = "Lanes";
  // The client's executable name (four CJK characters), built from code points so the source stays ASCII.
  static readonly string ClientExe = new string(new[] { (char)0x54D4, (char)0x54E9, (char)0x54D4, (char)0x54E9 }) + ".exe";
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
  const long LogLimit = 1024 * 1024;
  static readonly string[] Languages = { "en", "zh-Hant", "zh-Hans" };
  static readonly Dictionary<string, string[]> Notices = new Dictionary<string, string[]> {
    { "client-off", new[] { "notice.clientOff", "notice.clientOff.button", "launch" } },
    { "needs-restart", new[] { "notice.needsRestart", "notice.needsRestart.button", "restart" } },
    { "restart-failed", new[] { "notice.restartFailed", "notice.restartFailed.button", "restart" } },
  };

  static Window window;
  static Process controller;
  static HttpClient http;
  static System.Windows.Forms.NotifyIcon tray;
  static System.Windows.Forms.ToolStripItem trayOpen, trayQuit;
  static Segmented threadsSeg, languageSeg, regionSeg;
  static readonly List<double> history = new List<double>();
  static readonly List<Border> threadCells = new List<Border>();
  static Dictionary<string, string> strings = new Dictionary<string, string>(), english;
  static int failures = 0, noticeClickedAt;
  static bool rendering, polling, quitting, closeToTray, threadView;
  static string dir, logPath, language, current, noticeClickedState, updateState = "idle", announced;

  [STAThread]
  public static int Main(string[] args) {
    if (args.Length == 2 && args[0] == "--write-icon") { WriteIcon(args[1]); return 0; }
    var login = Array.IndexOf(args, "--login") >= 0;
    bool first;
    var mutex = new Mutex(true, "Lanes.Window", out first);
    if (!first) {
      // Lanes is already running (maybe only in the notification area): ask it to show its window.
      try { EventWaitHandle.OpenExisting("Lanes.Show").Set(); } catch (Exception) { }
      return 0;
    }
    var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Lanes.Show");

    dir = AppDomain.CurrentDomain.BaseDirectory;
    OpenLog();
    Log("Lanes started" + (login ? " at sign-in" : "") + ", folder: " + dir);
    english = LoadStrings("en");
    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) {
      Log("Lanes error: " + e.ExceptionObject);
      MessageBox.Show(T("error.crash") + "\n\n" + ((Exception)e.ExceptionObject).Message, Title);
    };
    // The folder may have moved since start-at-sign-in was turned on; keep the entry pointing here.
    try { if (AutoStartEnabled()) SetAutoStart(true); } catch (Exception e) { Log("Could not update the sign-in entry: " + e.Message); }

    var token = Guid.NewGuid().ToString("N");
    var node = File.Exists(System.IO.Path.Combine(dir, "node.exe")) ? System.IO.Path.Combine(dir, "node.exe") : "node.exe";
    var start = new ProcessStartInfo(node, "\"" + System.IO.Path.Combine(dir, "btr-local.cjs") + "\"") {
      UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
      RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = dir
    };
    start.EnvironmentVariables["BTR_TOKEN"] = token;
    start.EnvironmentVariables["LANES_LOGIN"] = login ? "1" : "0";
    var client = FindClient();
    if (client != null) start.EnvironmentVariables["LANES_CLIENT_EXE"] = client;
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    // Until the controller answers, the saved settings decide what closing does and which language shows.
    var initialLanguage = "en";
    try {
      var saved = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(System.IO.Path.Combine(dir, "settings.json")));
      object value;
      if (saved.TryGetValue("closeToTray", out value) && value is bool) closeToTray = (bool)value;
      if (saved.TryGetValue("language", out value) && Array.IndexOf(Languages, value as string) >= 0) initialLanguage = (string)value;
    } catch (Exception) { }
    ApplyLanguage(initialLanguage);
    try { controller = Process.Start(start); }
    catch (Exception e) {
      Log("Could not start Node.js (" + node + "): " + e.Message);
      MessageBox.Show(T("error.node"), Title);
      return 1;
    }
    // The controller reports through stderr; every line goes to lanes.log.
    controller.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
    controller.BeginErrorReadLine();
    controller.EnableRaisingEvents = true;
    controller.Exited += delegate { if (!quitting) Log("The controller exited unexpectedly, code " + controller.ExitCode); };

    http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    http.DefaultRequestHeaders.Add("X-BTR-Token", token);

    ApplyTheme(app.Resources);
    using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.xaml")) window = (Window)XamlReader.Load(xaml);
    window.Icon = RenderIcon(256);
    Wire();
    CreateTray();
    window.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) {
      if (quitting) return;
      if (closeToTray) { e.Cancel = true; HideToTray(); return; }
      Quit(false); // the window is already closing
    };
    new Thread(() => { while (showSignal.WaitOne()) window.Dispatcher.BeginInvoke(new Action(ShowWindow)); }) { IsBackground = true }.Start();
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    timer.Tick += delegate { Poll(); };
    timer.Start();
    // At sign-in Lanes only waits in the notification area.
    if (!login) window.Show();
    app.Run();
    GC.KeepAlive(mutex);
    return 0;
  }

  // ---- log: one file per run, next to Lanes.exe, always in English --------------------------------
  static readonly object logLock = new object();
  static StreamWriter logWriter;
  static string lastLine;
  static int repeats;
  static bool logFull;

  static void OpenLog() {
    logPath = System.IO.Path.Combine(dir, "lanes.log");
    try { logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true }; }
    catch (Exception) { logWriter = null; }
  }

  // The same line again (a repeating failure) is counted instead of written; the file stops at 1 MB.
  static void Log(string message) {
    lock (logLock) {
      if (logWriter == null || logFull) return;
      try {
        if (message == lastLine) { repeats++; return; }
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture);
        if (repeats > 0) { logWriter.WriteLine(stamp + "(previous line repeated " + repeats + " more times)"); repeats = 0; }
        lastLine = message;
        if (logWriter.BaseStream.Length + Encoding.UTF8.GetByteCount(stamp + message) + 2 > LogLimit) { logWriter.WriteLine(stamp + "The log reached 1 MB; nothing more is written in this run"); logFull = true; return; }
        logWriter.WriteLine(stamp + message);
      } catch (Exception) { }
    }
  }

  // ---- languages ----------------------------------------------------------------------------------
  static Dictionary<string, string> LoadStrings(string code) {
    var result = new Dictionary<string, string>();
    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lang." + code + ".json"))
    using (var reader = new StreamReader(stream, Encoding.UTF8)) {
      foreach (var pair in (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd())) result[pair.Key] = (string)pair.Value;
    }
    return result;
  }

  static string T(string key) {
    string value;
    return strings.TryGetValue(key, out value) || english.TryGetValue(key, out value) ? value : key;
  }
  static string T(string key, object arg) { return T(key).Replace("{0}", Convert.ToString(arg, CultureInfo.InvariantCulture)); }

  // XAML reads every string as {DynamicResource T.<key>}, so replacing the resources relabels the window.
  static void ApplyLanguage(string code) {
    language = code;
    strings = LoadStrings(code);
    var resources = Application.Current.Resources;
    foreach (var pair in english) resources["T." + pair.Key] = T(pair.Key);
    var cjk = code == "zh-Hans" ? "Microsoft YaHei UI" : "Microsoft JhengHei UI";
    resources["Body"] = new FontFamily("Segoe UI Variable Text, Segoe UI, " + cjk);
    resources["Display"] = new FontFamily("Segoe UI Variable Display, Segoe UI, " + cjk);
    if (tray != null) { trayOpen.Text = T("tray.open"); trayQuit.Text = T("tray.quit"); }
  }

  // ---- lifetime: window, notification area, exit -----------------------------------------------
  static void CreateTray() {
    tray = new System.Windows.Forms.NotifyIcon { Text = Title, Icon = ToIcon(RenderIcon(32)), Visible = true };
    tray.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs e) { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow(); };
    var menu = new System.Windows.Forms.ContextMenuStrip();
    trayOpen = menu.Items.Add(T("tray.open"), null, delegate { ShowWindow(); });
    trayQuit = menu.Items.Add(T("tray.quit"), null, delegate { Quit(); });
    tray.ContextMenuStrip = menu;
    // The only balloon is the one about a new version: clicking it opens the settings, where Update is.
    tray.BalloonTipClicked += delegate { ShowWindow(); ShowPage(true); };
  }

  static void ShowWindow() {
    if (quitting) return;
    window.Show();
    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
    window.Activate();
  }

  // Quietly: the setting already says what closing does.
  static void HideToTray() {
    window.Hide();
    Log("Window hidden to the notification area");
  }

  static void Quit(bool closeWindow = true) {
    if (quitting) return;
    quitting = true;
    Log("Lanes exiting");
    tray.Visible = false;
    tray.Dispose();
    // Closing the pipe tells the controller to stop accelerating and exit; in-flight downloads finish in the page.
    try { controller.StandardInput.Close(); controller.WaitForExit(4000); } catch (Exception) { }
    // Shutdown closes windows itself, so from inside Closing it has to wait until that close is done.
    if (closeWindow) { window.Close(); Application.Current.Shutdown(); }
    else window.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
  }

  static System.Drawing.Icon ToIcon(BitmapSource image) {
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(image));
    using (var stream = new MemoryStream()) {
      encoder.Save(stream);
      stream.Position = 0;
      using (var bitmap = new System.Drawing.Bitmap(stream)) return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
  }

  // ---- start at sign-in (the Run key is per computer, so it is read from Windows, not settings.json) --
  static bool AutoStartEnabled() {
    using (var run = Registry.CurrentUser.OpenSubKey(RunKey)) if (run == null || run.GetValue(Title) == null) return false;
    using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey)) {
      var state = approved == null ? null : approved.GetValue(Title) as byte[];
      // Task Manager's startup switch: an odd first byte means the user turned it off there.
      return state == null || state.Length == 0 || state[0] % 2 == 0;
    }
  }

  static void SetAutoStart(bool on) {
    using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) {
      if (on) run.SetValue(Title, "\"" + Assembly.GetExecutingAssembly().Location + "\" --login");
      else run.DeleteValue(Title, false);
    }
    if (!on) return;
    // Turning it on here also clears an "off" left by Task Manager, or Windows would still skip it.
    using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) if (approved != null) approved.DeleteValue(Title, false);
  }

  // The client's install folder from its uninstall record; null lets the controller use the default path.
  static string FindClient() {
    string[] keys = { @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili" };
    foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
      foreach (var name in keys) {
        using (var key = root.OpenSubKey(name)) {
          if (key == null) continue;
          var icon = (key.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim('"');
          foreach (var folder in new[] { key.GetValue("InstallLocation") as string, icon == "" ? null : System.IO.Path.GetDirectoryName(icon) }) {
            if (string.IsNullOrEmpty(folder)) continue;
            var exe = System.IO.Path.Combine(folder.Trim('"'), ClientExe);
            if (File.Exists(exe)) return exe;
          }
        }
      }
    }
    return null;
  }

  // ---- UI ---------------------------------------------------------------------------------------
  static T Find<T>(string name) where T : class { return window.FindName(name) as T; }

  static void ApplyTheme(ResourceDictionary r) {
    var light = true;
    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
      var value = key == null ? null : key.GetValue("AppsUseLightTheme");
      if (value is int) light = (int)value != 0;
    }
    // Apple's system colors, light and dark.
    var colors = light
      ? new Dictionary<string, string> { { "Bg", "#F2F2F7" }, { "Card", "#FFFFFF" }, { "Text", "#1D1D1F" }, { "Sub", "#86868B" }, { "Sep", "#E5E5EA" }, { "Seg", "#E3E3E8" }, { "Thumb", "#FFFFFF" }, { "Accent", "#0071E3" }, { "Green", "#34C759" }, { "Orange", "#FF9F0A" }, { "Stroke", "#1F000000" } }
      : new Dictionary<string, string> { { "Bg", "#1C1C1E" }, { "Card", "#2C2C2E" }, { "Text", "#F5F5F7" }, { "Sub", "#98989D" }, { "Sep", "#3A3A3C" }, { "Seg", "#3A3A3C" }, { "Thumb", "#636366" }, { "Accent", "#0A84FF" }, { "Green", "#30D158" }, { "Orange", "#FF9F0A" }, { "Stroke", "#33FFFFFF" } };
    foreach (var pair in colors) {
      var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
      brush.Freeze();
      r[pair.Key] = brush;
    }
  }

  // A segmented control assembled from XAML parts named <name>, <name>Thumb and <name>Labels.
  sealed class Segmented {
    readonly FrameworkElement host;
    readonly Border thumb;
    readonly object[] values;
    int index = -1;

    // labels: a "T.<key>" resource (follows the language) or plain text.
    public Segmented(string name, object[] values, string[] labels, Action<object> pick) {
      host = Find<FrameworkElement>(name);
      thumb = Find<Border>(name + "Thumb");
      this.values = values;
      var grid = Find<UniformGrid>(name + "Labels");
      for (var i = 0; i < values.Length; i++) {
        var value = values[i];
        var text = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        if (labels[i].StartsWith("T.")) text.SetResourceReference(TextBlock.TextProperty, labels[i]); else text.Text = labels[i];
        var button = new Button { Style = (Style)window.Resources["PlainButton"], Content = text, Name = name + "_" + i };
        button.Click += delegate { Select(value, true); pick(value); };
        grid.Children.Add(button);
      }
      host.SizeChanged += delegate { Move(index, false); };
    }

    public void Select(object value, bool animate) {
      var i = Array.FindIndex(values, v => v.ToString() == Convert.ToString(value, CultureInfo.InvariantCulture));
      if (i >= 0 && i != index) Move(i, animate && index >= 0);
    }

    void Move(int i, bool animate) {
      if (i < 0) return;
      index = i;
      var width = host.ActualWidth / values.Length;
      thumb.Width = width;
      var shift = (TranslateTransform)thumb.RenderTransform;
      if (!animate) { shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = i * width; return; }
      shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(i * width, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
  }

  static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
  static void Setting(string key, object value) { Post("/settings", Json(new Dictionary<string, object> { { key, value } })); }

  static void Wire() {
    MouseButtonEventHandler drag = delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
    Find<FrameworkElement>("Header").MouseLeftButtonDown += drag;
    Find<Button>("CloseButton").Click += delegate { window.Close(); };
    Find<Button>("MinimizeButton").Click += delegate { window.WindowState = WindowState.Minimized; };
    Find<Button>("SettingsButton").Click += delegate { ShowPage(true); };
    Find<Button>("BackButton").Click += delegate { ShowPage(false); };
    Find<Button>("ViewTotal").Click += delegate { ShowThreads(false); };
    Find<Button>("ViewThreads").Click += delegate { ShowThreads(true); };

    Switch("Enabled", on => Setting("enabled", on));
    Switch("CloseToTray", on => { closeToTray = on; Setting("closeToTray", on); });
    Switch("RestartRunning", on => Setting("restartRunningClient", on));
    var autoStart = Find<CheckBox>("AutoStart");
    autoStart.IsChecked = AutoStartEnabled();
    Switch("AutoStart", on => {
      try { SetAutoStart(on); Log(on ? "Start at sign-in turned on" : "Start at sign-in turned off"); }
      catch (Exception e) { Log("Could not change start at sign-in: " + e.Message); rendering = true; autoStart.IsChecked = !on; rendering = false; }
    });

    threadsSeg = new Segmented("ThreadsSeg", new object[] { "auto", 8, 16, 32, 64 }, new[] { "T.threads.auto", "8", "16", "32", "64" }, v => Setting("threads", v));
    languageSeg = new Segmented("LanguageSeg", Languages, new[] { "T.lang.en", "T.lang.zh-Hant", "T.lang.zh-Hans" }, v => { ApplyLanguage((string)v); Setting("language", v); });
    regionSeg = new Segmented("RegionSeg", new object[] { "auto", "overseas", "mainland" }, new[] { "T.region.auto", "T.region.overseas", "T.region.mainland" }, v => Setting("mode", v));

    var noticeButton = Find<Button>("NoticeButton");
    noticeButton.Click += delegate {
      noticeButton.IsEnabled = false; noticeClickedState = current; noticeClickedAt = Environment.TickCount;
      Post("/client", "{\"action\":\"" + noticeButton.Tag + "\"}");
    };
    Find<Button>("UpdateButton").Click += delegate { Post(updateState == "available" ? "/update/install" : "/update/check", "{}"); };
    Find<Button>("OpenLog").Click += delegate { try { Process.Start(logPath); } catch (Exception e) { Log("Could not open the log: " + e.Message); } };

    var accent = ((SolidColorBrush)Application.Current.Resources["Accent"]).Color;
    Find<GradientStop>("AreaTop").Color = Color.FromArgb(0x55, accent.R, accent.G, accent.B);
    Find<GradientStop>("AreaBottom").Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
  }

  // A switch reports only changes the user makes, not the ones Render applies.
  static void Switch(string name, Action<bool> changed) {
    var box = Find<CheckBox>(name);
    RoutedEventHandler handler = delegate { if (!rendering) changed(box.IsChecked == true); };
    box.Checked += handler;
    box.Unchecked += handler;
  }

  static void ShowPage(bool settings) {
    if (settings) {
      rendering = true; Find<CheckBox>("AutoStart").IsChecked = AutoStartEnabled(); rendering = false;
      // As tall as the main page, at least 500 so most settings show at once, and never past the screen.
      var page = Find<ScrollViewer>("SettingsPage");
      page.Height = Math.Min(Math.Max(Find<FrameworkElement>("MainPage").ActualHeight, 500), SystemParameters.WorkArea.Height - 190);
      page.ScrollToTop();
    }
    // The page not shown takes no space, so the window fits whichever page is open.
    Find<UIElement>("MainPage").Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
    Find<UIElement>("SettingsPage").Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    Find<UIElement>("MainTitle").Visibility = settings ? Visibility.Hidden : Visibility.Visible;
    Find<UIElement>("SettingsTitle").Visibility = settings ? Visibility.Visible : Visibility.Hidden;
    Find<UIElement>("SettingsButton").Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
    var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
    Find<UIElement>(settings ? "SettingsPage" : "MainPage").BeginAnimation(UIElement.OpacityProperty, fade);
    Find<UIElement>(settings ? "SettingsTitle" : "MainTitle").BeginAnimation(UIElement.OpacityProperty, fade);
  }

  static void ShowThreads(bool on) {
    threadView = on;
    Find<UIElement>("TotalView").Visibility = on ? Visibility.Hidden : Visibility.Visible;
    Find<UIElement>("ThreadView").Visibility = on ? Visibility.Visible : Visibility.Hidden;
    var shift = (TranslateTransform)Find<Border>("ViewThumb").RenderTransform;
    shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 52 : 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    Find<UIElement>(on ? "ThreadView" : "TotalView").BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    Find<ScrollViewer>("ThreadView").ScrollToTop();
  }

  // One cell per thread BTR may use right now: its number, speed in MB/s, and a bar relative to the fastest.
  static void RenderThreads(object[] slots, int limit, bool connected) {
    // Downloads go on while paused, so once any thread has worked the cells stay, sized to the current limit.
    var count = slots.Length > 0 ? limit : 0;
    var speeds = new double[count];
    var hosts = new string[count];
    double max = 0.5;
    for (var i = 0; i < count && i < slots.Length; i++) {
      var slot = (Dictionary<string, object>)slots[i];
      speeds[i] = connected ? Convert.ToDouble(slot["speed"]) / 1048576 : 0;
      hosts[i] = slot["host"] as string ?? "";
      max = Math.Max(max, speeds[i]);
    }
    var grid = Find<UniformGrid>("ThreadCells");
    while (threadCells.Count < count) { var cell = NewThreadCell(threadCells.Count + 1); threadCells.Add(cell); grid.Children.Add(cell); }
    var shown = 0;
    for (var i = 0; i < threadCells.Count; i++) {
      var cell = threadCells[i];
      if (cell.Visibility == Visibility.Visible) shown++;
      cell.Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
      if (i >= count) continue;
      var parts = (object[])cell.Tag;
      var text = (TextBlock)parts[0];
      var idle = speeds[i] < 0.01;
      text.Text = idle ? "—" : speeds[i] >= 10 ? speeds[i].ToString("0.0", CultureInfo.InvariantCulture) : speeds[i].ToString("0.00", CultureInfo.InvariantCulture);
      text.Foreground = (Brush)Application.Current.Resources[idle ? "Sub" : "Text"];
      ((ScaleTransform)parts[1]).ScaleX = speeds[i] / max;
      cell.ToolTip = string.IsNullOrEmpty(hosts[i]) ? null : T("thread.node", hosts[i]);
    }
    // Fewer threads than before (auto stepped down, or a lower setting): back to the top of the list.
    if (count < shown) Find<ScrollViewer>("ThreadView").ScrollToTop();
    Find<TextBlock>("SpeedLabel").Text = !threadView ? T("speed.label") : count == 0 ? T("speed.threadsWaiting") : T("speed.threadsLabel");
  }

  static Border NewThreadCell(int number) {
    var resources = Application.Current.Resources;
    var label = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 11, Foreground = (Brush)resources["Sub"], VerticalAlignment = VerticalAlignment.Center };
    var speed = new TextBlock { Text = "—", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    var top = new DockPanel();
    DockPanel.SetDock(label, Dock.Left);
    top.Children.Add(label);
    top.Children.Add(speed);
    var scale = new ScaleTransform(0, 1);
    var bars = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
    bars.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = (Brush)resources["Sep"] });
    bars.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = (Brush)resources["Accent"], RenderTransform = scale });
    var stack = new StackPanel();
    stack.Children.Add(top);
    stack.Children.Add(bars);
    return new Border { Padding = new Thickness(6, 6, 6, 8), Child = stack, Tag = new object[] { speed, scale } };
  }

  static async void Post(string path, string json) {
    try { await http.PostAsync(Api + path, new StringContent(json, Encoding.UTF8, "application/json")); }
    catch (Exception e) { Log("Could not send a request to the controller: " + e.Message); }
  }

  static async void Poll() {
    if (polling || quitting) return;
    polling = true;
    try {
      var text = await http.GetStringAsync(Api + "/status");
      failures = 0;
      Render((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(text));
    } catch (Exception) {
      // The controller is still starting, or it has gone away.
      if (++failures > 6 || controller.HasExited) ShowStatus(T(controller.HasExited ? "status.stopped" : "status.starting"), "Orange");
    } finally { polling = false; }
  }

  static void ShowStatus(string text, string color) {
    Find<TextBlock>("Status").Text = text;
    Find<Ellipse>("Dot").Fill = (Brush)Application.Current.Resources[color];
  }

  static void Render(Dictionary<string, object> s) {
    rendering = true;
    try {
      var state = (string)s["state"];
      current = state;
      var settings = (Dictionary<string, object>)s["settings"];
      var chosen = settings["language"] as string;
      var wanted = Array.IndexOf(Languages, chosen) >= 0 ? chosen : "en";
      if (wanted != language) ApplyLanguage(wanted);
      languageSeg.Select(wanted, false);
      regionSeg.Select(settings["mode"], true);
      var enabled = (bool)settings["enabled"];
      var connected = state == "connected";
      var playing = (bool)s["playing"];
      closeToTray = (bool)settings["closeToTray"];
      tray.Text = connected && enabled && playing ? T("tray.accelerating") : Title;

      if (state == "starting") ShowStatus(T("status.clientStarting"), "Orange");
      else if (!connected) ShowStatus(T(state == "connecting" ? "status.connecting" : "status.notConnected"), "Orange");
      else if ((bool)s["foreign"]) ShowStatus(T("status.foreign"), "Orange");
      else if (!enabled) ShowStatus(T("status.off"), "Sub");
      else if ((bool)s["suspended"]) ShowStatus(T("status.suspended"), "Orange");
      else ShowStatus(T(playing ? "status.accelerating" : "status.ready"), "Green");

      var mbps = connected ? Convert.ToDouble(s["speed"]) / 1048576 : 0;
      history.Add(mbps);
      if (history.Count > 60) history.RemoveAt(0);
      var speed = Find<TextBlock>("Speed");
      speed.Text = mbps >= 100 ? mbps.ToString("0", CultureInfo.InvariantCulture) : mbps.ToString("0.0", CultureInfo.InvariantCulture);
      speed.Foreground = (Brush)Application.Current.Resources[mbps < 0.05 ? "Sub" : "Text"];
      DrawChart();
      Find<TextBlock>("Requests").Text = T("speed.requests", s["requests"]);
      var threads = Convert.ToInt32(s["threads"]);
      Find<TextBlock>("ThreadsNow").Text = playing ? T("speed.threadCount", threads) : "";
      object slotList;
      RenderThreads(s.TryGetValue("threadSpeeds", out slotList) && slotList is object[] ? (object[])slotList : new object[0], connected ? threads : 0, connected);

      Find<CheckBox>("Enabled").IsChecked = enabled;
      Find<CheckBox>("CloseToTray").IsChecked = closeToTray;
      Find<CheckBox>("RestartRunning").IsChecked = (bool)settings["restartRunningClient"];
      threadsSeg.Select(settings["threads"], true);
      RenderUpdate((string)s["version"], (Dictionary<string, object>)s["update"]);

      // Without a connected client there is no speed to show; the notice takes its place.
      string[] notice;
      var hasNotice = Notices.TryGetValue(state, out notice);
      Find<UIElement>("Notice").Visibility = hasNotice ? Visibility.Visible : Visibility.Collapsed;
      Find<UIElement>("SpeedCard").Visibility = hasNotice ? Visibility.Collapsed : Visibility.Visible;
      if (hasNotice) {
        Find<TextBlock>("NoticeText").Text = T(notice[0]);
        var button = Find<Button>("NoticeButton");
        button.Content = T(notice[1]); button.Tag = notice[2];
        // Pressed: stays off while the same state holds, for at most 5 s, so a launch that failed (back to the
        // same notice) can be tried again.
        button.IsEnabled = state != noticeClickedState || Environment.TickCount - noticeClickedAt > 5000;
      }
    } finally { rendering = false; }
  }

  static void RenderUpdate(string version, Dictionary<string, object> update) {
    updateState = (string)update["state"];
    var latest = (string)update["latest"];
    Find<TextBlock>("VersionText").Text = "Lanes " + T("settings.version", version);
    var status = Find<TextBlock>("UpdateStatus");
    var keys = new Dictionary<string, string> { { "checking", "update.checking" }, { "latest", "update.latest" }, { "available", "update.available" }, { "downloading", "update.downloading" }, { "ready", "update.ready" }, { "failed", "update.failed" }, { "install-failed", "update.installFailed" } };
    string key;
    status.Visibility = keys.TryGetValue(updateState, out key) ? Visibility.Visible : Visibility.Collapsed;
    object progress;
    if (key != null) status.Text = T(key, updateState == "downloading" && update.TryGetValue("progress", out progress) ? progress : latest);
    status.Foreground = (Brush)Application.Current.Resources[updateState == "available" ? "Accent" : updateState.EndsWith("failed") ? "Orange" : "Sub"];
    Find<TextBlock>("UpdateButtonText").Text = T(updateState == "available" ? "update.install" : "update.check");
    Find<Button>("UpdateButton").IsEnabled = updateState != "checking" && updateState != "downloading" && updateState != "ready";
    Find<UIElement>("UpdateDot").Visibility = updateState == "available" ? Visibility.Visible : Visibility.Collapsed;
    // Found in the background (Lanes often runs hidden): say so once per version, unless the window is in view.
    if (updateState == "available" && latest != announced) {
      announced = latest;
      if (!window.IsVisible || window.WindowState == WindowState.Minimized)
        tray.ShowBalloonTip(5000, T("tray.updateTitle"), T("tray.update", latest), System.Windows.Forms.ToolTipIcon.None);
    }
    // The new files go in once Lanes has exited; the controller left a script waiting for that.
    if (updateState == "ready" && !quitting) Quit();
  }

  static void DrawChart() {
    var chart = Find<FrameworkElement>("Chart");
    double w = chart.ActualWidth, h = chart.ActualHeight, max = 1;
    foreach (var v in history) max = Math.Max(max, v);
    max *= 1.15;
    if (w <= 0 || history.Count == 0) return;
    var inv = CultureInfo.InvariantCulture;
    var line = new StringBuilder();
    double x0 = 0, x = 0;
    for (var i = 0; i < history.Count; i++) {
      x = (i + 60 - history.Count) / 59.0 * w;
      var y = h - history[i] / max * h;
      if (i == 0) x0 = x;
      line.Append(i == 0 ? "M" : "L").Append(x.ToString("0.0", inv)).Append(",").Append(y.ToString("0.0", inv));
    }
    Find<System.Windows.Shapes.Path>("ChartLine").Data = Geometry.Parse(line.ToString());
    Find<System.Windows.Shapes.Path>("ChartArea").Data = Geometry.Parse(line + "L" + x.ToString("0.0", inv) + "," + h.ToString("0.0", inv) + "L" + x0.ToString("0.0", inv) + "," + h.ToString("0.0", inv) + "Z");
  }

  // The app icon: a dark rounded square with a white L and three blue speed lines, drawn in code so there is one
  // source for it.
  static BitmapSource RenderIcon(int size) {
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen()) {
      var scale = size / 64.0;
      dc.PushTransform(new ScaleTransform(scale, scale));
      dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), null, new Rect(0, 0, 64, 64), 15, 15);
      var letter = new Pen(Brushes.White, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
      dc.DrawGeometry(null, letter, Geometry.Parse("M31 15V48H51"));
      var speed = new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
      dc.DrawLine(speed, new Point(12, 22.5), new Point(22.5, 22.5));
      dc.DrawLine(speed, new Point(9.5, 31.5), new Point(22.5, 31.5));
      dc.DrawLine(speed, new Point(12, 40.5), new Point(22.5, 40.5));
      dc.Pop();
    }
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return bitmap;
  }

  // An .ico holding PNG images (supported since Windows Vista), for the exe's own icon.
  static void WriteIcon(string path) {
    int[] sizes = { 256, 48, 32, 16 };
    var images = new List<byte[]>();
    foreach (var size in sizes) {
      var encoder = new PngBitmapEncoder();
      encoder.Frames.Add(BitmapFrame.Create(RenderIcon(size)));
      using (var stream = new MemoryStream()) { encoder.Save(stream); images.Add(stream.ToArray()); }
    }
    using (var output = new BinaryWriter(File.Create(path))) {
      output.Write((short)0); output.Write((short)1); output.Write((short)sizes.Length);
      var offset = 6 + 16 * sizes.Length;
      for (var i = 0; i < sizes.Length; i++) {
        output.Write((byte)(sizes[i] % 256)); output.Write((byte)(sizes[i] % 256)); output.Write((byte)0); output.Write((byte)0);
        output.Write((short)1); output.Write((short)32); output.Write(images[i].Length); output.Write(offset);
        offset += images[i].Length;
      }
      foreach (var image in images) output.Write(image);
    }
  }
}
