// Developer checks, run by check.cjs: Lanes.exe --self-check. Exits 1 on the first failure. The page-side JavaScript is
// checked by check.cjs itself, on the exact scripts written by Lanes.exe --dump-page-scripts <folder>.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

public static class SelfCheck {
  static void Check(bool condition, string what) { if (!condition) throw new Exception("failed: " + what); }

  public static int Run(string folder) {
    int exit = 0;
    var dispatcher = Dispatcher.CurrentDispatcher;
    var frame = new DispatcherFrame();
    // The controller expects the dispatcher's single thread, as in the window.
    dispatcher.BeginInvoke(new Action(async () => {
      try { await RunAll(folder); Console.WriteLine("self-check: ok"); }
      catch (Exception e) { Console.WriteLine(e.Message); Console.WriteLine(e.StackTrace); exit = 1; }
      frame.Continue = false;
    }));
    Dispatcher.PushFrame(frame);
    Console.Out.Flush();
    return exit;
  }

  public static int DumpPageScripts(string folder, string output) {
    var controller = new Controller(folder, s => { }, new FakeSystem());
    controller.Settings = Controller.Defaults(); // inject.js carries the settings; check.cjs expects the defaults
    Directory.CreateDirectory(output);
    var utf8 = new UTF8Encoding(false);
    File.WriteAllText(Path.Combine(output, "inject.js"), controller.PageScript(), utf8);
    File.WriteAllText(Path.Combine(output, "meter.js"), Controller.Resource("page.meter.js"), utf8);
    File.WriteAllText(Path.Combine(output, "poll.js"), controller.PollScript(), utf8);
    var auto = Controller.Defaults();
    var mainland = Controller.Merged(auto, new Dictionary<string, object> { { "mode", "mainland" }, { "threads", 16 } });
    File.WriteAllText(Path.Combine(output, "push-auto.js"), Controller.PushScript(auto), utf8);
    File.WriteAllText(Path.Combine(output, "push-mainland.js"), Controller.PushScript(mainland), utf8);
    return 0;
  }

  static async Task RunAll(string folder) {
    TakeoverRule(); Console.WriteLine("takeover rule: ok");
    Versions(); Console.WriteLine("version comparison: ok");
    SettingsFiles(); Console.WriteLine("settings: ok");
    BtrSettings(); Console.WriteLine("btr settings: ok");
    Slots(); Console.WriteLine("thread slots: ok");
    ReleaseAssets(); Console.WriteLine("release asset: ok");
    await Downloads(); Console.WriteLine("downloads: ok");
    ApplyScriptBranches(); Console.WriteLine("update script branches: ok");
    var sandbox = Sandbox(folder);
    try {
      await CdpOrder(sandbox); Console.WriteLine("cdp: ok");
      await Polling(sandbox); Console.WriteLine("polling: ok");
      ClientProcesses(); Console.WriteLine("client processes: ok");
      await Restarts(sandbox); Console.WriteLine("restarts: ok");
    } finally {
      try { Directory.Delete(sandbox, true); } catch (Exception) { }
    }
  }

  // A throwaway Lanes folder (version.json and BTR's files) so the checks never touch the real settings.json.
  static string Sandbox(string folder) {
    var dir = Path.Combine(Path.GetTempPath(), "lanes-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(Path.Combine(dir, "vendor", "btr"));
    File.Copy(Path.Combine(folder, "version.json"), Path.Combine(dir, "version.json"));
    foreach (var file in Directory.GetFiles(Path.Combine(folder, "vendor", "btr"))) File.Copy(file, Path.Combine(dir, "vendor", "btr", Path.GetFileName(file)));
    return dir;
  }

  // ---- rules -------------------------------------------------------------------------------------------------------
  static void TakeoverRule() {
    const double now = 1000000;
    Func<long, ClientRun> run = started => new ClientRun { Id = (int)(started % 1000) + 1, StartedMs = started };
    var fresh = run((long)now - 3000);
    var old = run((long)now - 600000);
    var none = new HashSet<string>();
    Check(Controller.ShouldTakeOver(fresh, null, none, double.NegativeInfinity, now, false, 0), "a client opened while Lanes runs, 3 s ago");
    Check(!Controller.ShouldTakeOver(null, null, none, double.NegativeInfinity, now, false, 0), "no client");
    Check(!Controller.ShouldTakeOver(old, null, none, double.NegativeInfinity, now, false, 0), "an older client may be playing");
    Check(!Controller.ShouldTakeOver(fresh, fresh.Key, none, double.NegativeInfinity, now, false, 0), "the client already running when Lanes started");
    Check(Controller.ShouldTakeOver(old, old.Key, none, double.NegativeInfinity, now, true, 0), "with the setting on, the running client is restarted");
    Check(!Controller.ShouldTakeOver(fresh, null, new HashSet<string> { fresh.Key }, double.NegativeInfinity, now, false, 0), "a client run already tried once");
    Check(!Controller.ShouldTakeOver(fresh, null, none, now - 5000, now, false, 0), "too soon after the last takeover");
    Check(!Controller.ShouldTakeOver(old, null, none, double.NegativeInfinity, now, true, 2), "two takeovers in a row that never connected stop them");
  }

  static void Versions() {
    Check(Controller.IsNewer("1.0.1", "1.0.0") && Controller.IsNewer("1.10.0", "1.9.9"), "newer versions");
    Check(!Controller.IsNewer("1.0.0", "1.0.0") && !Controller.IsNewer("0.9.9", "1.0.0"), "same or older versions");
  }

  static void SettingsFiles() {
    var defaults = Controller.Defaults();
    var patched = Controller.Merged(defaults, new Dictionary<string, object> { { "enabled", "yes" }, { "threads", 12 }, { "mode", "custom" }, { "language", "fr" }, { "closeToTray", true }, { "clientExe", @"relative\x.exe" } });
    Check(Json.Bool(patched["enabled"]) && (string)patched["threads"] == "auto" && (string)patched["mode"] == "auto" && (string)patched["language"] == "en", "wrong types and unknown values are ignored");
    Check(Json.Bool(patched["closeToTray"]), "a valid value is taken");
    Check((string)patched["clientExe"] == "", "a client path must be absolute and named like the client");
    var exe = @"D:\Apps\bilibili\" + Client.ExeName;
    Check((string)Controller.Merged(defaults, new Dictionary<string, object> { { "clientExe", exe } })["clientExe"] == exe, "an absolute client path is kept");
    foreach (var relative in new[] { "D:Apps\\" + Client.ExeName, "\\Apps\\" + Client.ExeName })
      Check((string)Controller.Merged(defaults, new Dictionary<string, object> { { "clientExe", relative } })["clientExe"] == "", "a path relative to the current directory is not taken: " + relative);
    Check(Controller.ArgumentPath(@"D:\Apps\Lanes\") == @"D:\Apps\Lanes" && Controller.ArgumentPath(@"D:\") == @"D:\.", "the update script gets the folder without a trailing backslash, and a drive root as D:\\.");
    Check(Json.Same(Controller.Merged(defaults, new Dictionary<string, object> { { "threads", 16 } })["threads"], 16), "a thread count is kept");
    Check((string)Controller.Merged(defaults, new Dictionary<string, object> { { "threads", "16" } })["threads"] == "auto", "a thread count written as text is not");
    var file = Path.Combine(Path.GetTempPath(), "lanes-check-" + Guid.NewGuid().ToString("N") + ".json");
    try {
      foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode }) {
        File.WriteAllText(file, "{\"mode\":\"mainland\"}", encoding);
        Check((string)Json.Get(Json.Obj(Json.ReadFile(file)), "mode") == "mainland", "settings.json read as " + encoding.EncodingName + (encoding.GetPreamble().Length > 0 ? " with a byte order mark" : ""));
      }
    } finally { File.Delete(file); }
  }

  static void BtrSettings() {
    var auto = Controller.Defaults();
    var want = Controller.ToBtr(auto);
    Check((string)want["mode"] == "custom" && Json.Bool(want["autoConcurrency"]) && !want.ContainsKey("concurrency"), "region auto is BTR's custom mode with automatic threads");
    var manual = Controller.ToBtr(Controller.Merged(auto, new Dictionary<string, object> { { "mode", "mainland" }, { "threads", 32 } }));
    Check((string)manual["mode"] == "mainland" && !Json.Bool(manual["autoConcurrency"]) && Json.Same(manual["concurrency"], 32), "a manual region and thread count");
    var page = new Dictionary<string, object> { { "enabled", true }, { "mode", "custom" }, { "autoConcurrency", true }, { "concurrency", 8 }, { "hostsOk", true } };
    Check(!Controller.NeedsPush(page, want), "no push while the page matches (its unused concurrency does not count)");
    Check(Controller.NeedsPush(With(page, "hostsOk", false), want), "a custom list other than BTR's full one is replaced");
    Check(Controller.NeedsPush(With(page, "mode", "overseas"), want), "another mode is replaced");
    var pageManual = new Dictionary<string, object> { { "enabled", true }, { "mode", "mainland" }, { "autoConcurrency", false }, { "concurrency", 32m }, { "hostsOk", true } };
    Check(!Controller.NeedsPush(pageManual, manual), "numbers compare by value");
    var script = Controller.PushScript(auto);
    Check(script.Contains("const want = {\"enabled\":true,\"mode\":\"custom\",\"autoConcurrency\":true};"), "the push script carries the settings");
  }

  static Dictionary<string, object> With(Dictionary<string, object> map, string key, object value) {
    var copy = new Dictionary<string, object>(map);
    copy[key] = value;
    return copy;
  }

  static void Slots() {
    var memo = new Controller.SlotMemo();
    var first = Controller.SlotStep(memo, new object[] { 1000, "a.bilivideo.com", 1 }, 1);
    Check(first.Rate == 0 && first.Restart, "a slot seen for the first time only sets its baseline");
    var other = Controller.SlotStep(memo, new object[] { 500, "b.bilivideo.com", 2 }, 1);
    Check(other.Rate == 500 && other.Restart && other.Host == "b.bilivideo.com", "a new run counts only its own bytes and starts over");
    var same = Controller.SlotStep(memo, new object[] { 1500, "b.bilivideo.com", 2 }, 0.5);
    Check(same.Rate == 2000 && !same.Restart, "the same run keeps growing");
    var older = new Controller.SlotMemo();
    Controller.SlotStep(older, new object[] { 100, "x", null }, 1);
    var growth = Controller.SlotStep(older, new object[] { 300, "x", null }, 1);
    Check(growth.Rate == 200 && !growth.Restart, "a page from an older Lanes counts as one run");
    var tab = new Controller.PageMemo();
    Controller.SlotMemos(tab, 1.5).Add(new Controller.SlotMemo());
    Check(Controller.SlotMemos(tab, 1.5).Count == 1, "the same page keeps its baselines");
    Check(Controller.SlotMemos(tab, 2.5).Count == 0, "a reloaded page (new meter id) starts with fresh baselines");
  }

  static void ReleaseAssets() {
    const string prefix = "https://github.com/owner/lanes/releases/download/";
    Func<string, string, Dictionary<string, object>> asset = (name, url) => new Dictionary<string, object> { { "name", name }, { "size", 3 }, { "browser_download_url", url } };
    var release = new Dictionary<string, object> { { "assets", new object[] { asset("Lanes-9.0.0.zip", prefix + "v9.0.0/Lanes-9.0.0.zip"), asset("other.zip", prefix + "v9.0.0/other.zip") } } };
    Check(Controller.ReleaseAsset(release, "9.0.0", prefix) != null, "the release's package");
    Check(Controller.ReleaseAsset(release, "9.0.0", prefix.Replace("/lanes/", "/Lanes/")) != null, "the repository name in another case (GitHub links use its current spelling)");
    var foreign = new Dictionary<string, object> { { "assets", new object[] { asset("Lanes-9.0.0.zip", "https://example.com/Lanes-9.0.0.zip") } } };
    Check(Controller.ReleaseAsset(foreign, "9.0.0", prefix) == null, "a package from elsewhere is not taken");
    Check(Controller.ReleaseAsset(new Dictionary<string, object>(), "9.0.0", prefix) == null, "a release without assets");
  }

  // ---- downloads, against a small HTTP server on a loopback port ------------------------------------------------------
  // Answers every request with chunks of the given sizes, one every gapMs; ends the body or goes silent.
  static TcpListener Serve(int[] chunks, int gapMs, bool end, long length) {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    Task.Run(async () => {
      try {
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream()) {
          var buffer = new byte[4096];
          await stream.ReadAsync(buffer, 0, buffer.Length);
          var head = "HTTP/1.1 200 OK\r\nConnection: close\r\n" + (length > 0 ? "Content-Length: " + length + "\r\n" : "") + "\r\n";
          var bytes = Encoding.ASCII.GetBytes(head);
          await stream.WriteAsync(bytes, 0, bytes.Length);
          foreach (var size in chunks) { await Task.Delay(gapMs); await stream.WriteAsync(new byte[size], 0, size); await stream.FlushAsync(); }
          if (!end) await Task.Delay(10000);
        }
      } catch (Exception) { }
    });
    return listener;
  }

  static async Task Downloads() {
    var file = Path.Combine(Path.GetTempPath(), "lanes-check-" + Guid.NewGuid().ToString("N") + ".bin");
    try {
      var seen = new List<int>();
      var server = Serve(new[] { 500, 500 }, 60, true, 0);
      await Controller.Download("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", file, 1000, p => seen.Add(p), 1000);
      server.Stop();
      Check(new FileInfo(file).Length == 1000 && seen.Last() == 100 && seen.Contains(50), "percents from GitHub's asset size");
      seen.Clear();
      server = Serve(new[] { 250, 750 }, 60, true, 1000);
      await Controller.Download("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", file, 0, p => seen.Add(p), 1000);
      server.Stop();
      Check(seen.Contains(25) && seen.Last() == 100, "percents from Content-Length when there is no asset size");
      server = Serve(Enumerable.Repeat(10, 12).ToArray(), 60, true, 0);
      await Controller.Download("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", file, 120, p => { }, 200);
      server.Stop();
      Check(new FileInfo(file).Length == 120, "a download that keeps moving is not cut: 720 ms with a 200 ms stall limit");
      server = Serve(new[] { 10 }, 10, false, 0);
      var stalled = await Fails(Controller.Download("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", file, 100, p => { }, 300));
      server.Stop();
      Check(stalled.Contains("no data for 0.3 s"), "a stalled download stops: " + stalled);
      server = Serve(new[] { 10 }, 10, true, 0);
      var shortened = await Fails(Controller.Download("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", file, 100, p => { }, 1000));
      server.Stop();
      Check(shortened.Contains("download incomplete: 10 of 100 bytes"), "a short download fails: " + shortened);
    } finally { try { File.Delete(file); } catch (Exception) { } }
  }

  static async Task<string> Fails(Task task) {
    try { await task; return "(did not fail)"; } catch (Exception e) { return e.Message; }
  }

  // ---- the update script, with robocopy replaced so every branch is reached ------------------------------------------
  static void ApplyScriptBranches() {
    var dir = Path.Combine(Path.GetTempPath(), "lanes-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(dir);
    try {
      var script = string.Join("\r\n", Controller.ApplyScript.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(line => !line.StartsWith("Start-Process")));
      File.WriteAllText(Path.Combine(dir, "apply.ps1"), script, new UTF8Encoding(false));
      File.WriteAllText(Path.Combine(dir, "branch.ps1"), string.Join("\r\n", new[] {
        "param([string]$Codes, [string]$Root)",
        "$script:fake = New-Object System.Collections.Queue; foreach ($n in $Codes -split '-') { $script:fake.Enqueue([int]$n) }",
        "function robocopy { New-Item -ItemType Directory -Force $args[1] | Out-Null; $global:LASTEXITCODE = $script:fake.Dequeue() }",
        "$work = Join-Path $Root 'lanes-update-test'; New-Item -ItemType Directory -Force (Join-Path $work 'files\\Lanes'), (Join-Path $Root 'target') | Out-Null",
        ". (Join-Path $PSScriptRoot 'apply.ps1') -Source (Join-Path $work 'files\\Lanes') -Target (Join-Path $Root 'target') -Wait 1 -Work $work",
        "(Get-Content (Join-Path $Root 'target\\update-result.txt')) + ' | backup kept: ' + (Test-Path (Join-Path $work 'files\\backup'))"
      }), new UTF8Encoding(false));
      var cases = new Dictionary<string, string> {
        { "16", "Update not applied: the current version could not be backed up (robocopy exit 16) | backup kept: False" },
        { "1-1", "Update applied | backup kept: False" },
        { "1-8-1", "Update failed (robocopy exit 8); the previous version was restored | backup kept: False" },
        { "1-8-8", "Update failed (robocopy exit 8) and restoring the previous version failed too (robocopy exit 8); the previous version is kept in" }
      };
      foreach (var pair in cases) {
        var root = Path.Combine(dir, pair.Key);
        var start = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(dir, "branch.ps1") + "\" -Codes " + pair.Key + " -Root \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        string output;
        using (var process = Process.Start(start)) { output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); }
        Check(output.StartsWith(pair.Value.Split('|')[0].Trim()) && (pair.Key != "1-8-8" || output.EndsWith("backup kept: True")) && (pair.Key == "1-8-8" || output == pair.Value), "update script branch " + pair.Key + ": " + output);
      }

      // A failed update that had already added files: the restore brings the old files back and removes what the update
      // added, and nothing else. Here robocopy copies for real (honouring /XF) and returns 1, 8, 1.
      File.WriteAllText(Path.Combine(dir, "restore.ps1"), @"param([string]$Root)
$script:fake = New-Object System.Collections.Queue; foreach ($n in 1, 8, 1) { $script:fake.Enqueue($n) }
function robocopy {
  $from = (Get-Item -LiteralPath $args[0]).FullName; $to = $args[1]; $skip = @()
  $i = [array]::IndexOf($args, '/XF'); if ($i -ge 0) { for ($j = $i + 1; $j -lt $args.Count -and -not ([string]$args[$j]).StartsWith('/'); $j++) { $skip += $args[$j] } }
  New-Item -ItemType Directory -Force $to | Out-Null
  Get-ChildItem -LiteralPath $from -Recurse -File | Where-Object { $skip -notcontains $_.Name } | ForEach-Object {
    $dest = Join-Path $to $_.FullName.Substring($from.Length).TrimStart([char]92)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null; Copy-Item -LiteralPath $_.FullName $dest -Force }
  $global:LASTEXITCODE = $script:fake.Dequeue()
}
function Put($path, $text) { New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; Set-Content -LiteralPath $path -Value $text }
$work = Join-Path $Root 'lanes-update-test'; $source = Join-Path $work 'files\Lanes'; $target = Join-Path $Root 'target'
Put (Join-Path $target 'version.json') 'old'; Put (Join-Path $target 'vendor\btr\a.js') 'old'; Put (Join-Path $target 'settings.json') 'mine'; Put (Join-Path $target 'notes.txt') 'mine'
Put (Join-Path $source 'version.json') 'new'; Put (Join-Path $source 'vendor\btr\a.js') 'new'; Put (Join-Path $source 'vendor\btr\added.js') 'new'; Put (Join-Path $source 'added\x.txt') 'new'; Put (Join-Path $source 'settings.json') 'package'
. (Join-Path $PSScriptRoot 'apply.ps1') -Source $source -Target $target -Wait 1 -Work $work
Get-ChildItem -LiteralPath $target -Recurse | ForEach-Object { $_.FullName.Substring($target.Length + 1) + '=' + $(if ($_.PSIsContainer) { 'dir' } else { Get-Content -LiteralPath $_.FullName }) }
", new UTF8Encoding(false));
      var restore = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(dir, "restore.ps1") + "\" -Root \"" + Path.Combine(dir, "restore") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
      List<string> left;
      using (var process = Process.Start(restore)) { left = process.StandardOutput.ReadToEnd().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).ToList(); process.WaitForExit(); }
      var want = new[] { "notes.txt=mine", "settings.json=mine", "update-result.txt=Update failed (robocopy exit 8); the previous version was restored", "vendor=dir", "vendor\\btr=dir", "vendor\\btr\\a.js=old", "version.json=old" };
      Check(left.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(want.OrderBy(s => s, StringComparer.Ordinal)), "a failed update is rolled back without what it added: " + string.Join("; ", left));
    } finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
  }

  // ---- CDP, with a fake connection that records what is sent --------------------------------------------------------
  sealed class FakeTransport : ICdpTransport {
    public readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();
    public readonly HashSet<string> SilentSessions = new HashSet<string>(); // paused pages: no answers
    public Func<Dictionary<string, object>, object> EvaluateAnswer = m => 0;
    public string FailMethod; // answered with a CDP error
    readonly Queue<string> incoming = new Queue<string>();
    TaskCompletionSource<string> waiting;

    public Task SendAsync(string text) {
      var message = Json.Obj(Json.Read(text));
      Sent.Add(message);
      var session = Json.Get(message, "sessionId") as string;
      if (session == null || !SilentSessions.Contains(session)) {
        object result = new Dictionary<string, object>();
        if (Json.Str(Json.Get(message, "method")) == "Runtime.evaluate") result = new Dictionary<string, object> { { "result", new Dictionary<string, object> { { "value", EvaluateAnswer(message) } } } };
        if (Json.Str(Json.Get(message, "method")) == FailMethod) Push(Json.Write(new Dictionary<string, object> { { "id", Json.Get(message, "id") }, { "error", new Dictionary<string, object> { { "message", "refused" } } } }));
        else Push(Json.Write(new Dictionary<string, object> { { "id", Json.Get(message, "id") }, { "result", result } }));
      }
      return Task.FromResult(0);
    }
    public void Push(string text) {
      if (waiting != null) { var w = waiting; waiting = null; w.SetResult(text); } else incoming.Enqueue(text);
    }
    public Task<string> ReceiveAsync() {
      if (incoming.Count > 0) return Task.FromResult(incoming.Dequeue());
      waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      return waiting.Task;
    }
    public void Abort() { if (waiting != null) waiting.TrySetResult(null); }
    public void Event(string method, Dictionary<string, object> parameters) {
      Push(Json.Write(new Dictionary<string, object> { { "method", method }, { "params", parameters } }));
    }
    public List<string> Methods(string session) {
      return Sent.Where(m => Json.Get(m, "sessionId") as string == session).Select(m => Json.Str(Json.Get(m, "method"))).ToList();
    }
  }

  static Dictionary<string, object> Attached(string sessionId, string type, bool waiting) {
    return new Dictionary<string, object> { { "sessionId", sessionId }, { "waitingForDebugger", waiting }, { "targetInfo", new Dictionary<string, object> { { "type", type }, { "url", "https://example/" } } } };
  }

  static async Task Settle() { await Task.Delay(100); }

  static async Task CdpOrder(string sandbox) {
    var controller = new Controller(sandbox, s => { }, new FakeSystem());
    var fake = new FakeTransport();
    await controller.Attach(new Cdp(fake));
    var autoAttach = fake.Sent[0];
    var parameters = Json.Obj(Json.Get(autoAttach, "params"));
    Check(Json.Str(Json.Get(autoAttach, "method")) == "Target.setAutoAttach" && Json.Bool(Json.Get(parameters, "waitForDebuggerOnStart")) && Json.Get(parameters, "filter") == null, "auto-attach waits for every new target, without a filter");

    // A browser that refuses auto-attach: the connection is dropped so it is tried again, not kept as "connected".
    // The controller is quitting first, so the dropped connection does not start watching the real debugging port.
    var refused = new Controller(sandbox, s => { }, new FakeSystem());
    await refused.Shutdown();
    var attachError = await Fails(refused.Attach(new Cdp(new FakeTransport { FailMethod = "Target.setAutoAttach" })));
    Check(attachError == "refused" && !refused.Connected, "a connection whose auto-attach fails is dropped: " + attachError + ", connected " + refused.Connected);

    // A new player window stays paused and answers nothing: all three commands must go out without waiting.
    fake.SilentSessions.Add("page-1");
    fake.Event("Target.attachedToTarget", Attached("page-1", "page", true));
    await Settle();
    Check(fake.Methods("page-1").SequenceEqual(new[] { "Page.enable", "Page.addScriptToEvaluateOnNewDocument", "Runtime.runIfWaitingForDebugger" }), "a new page gets enable, the bundle and its release back to back: " + string.Join(", ", fake.Methods("page-1")));
    var source = Json.Str(Json.Get(Json.Obj(Json.Get(fake.Sent.First(m => Json.Str(Json.Get(m, "method")) == "Page.addScriptToEvaluateOnNewDocument"), "params")), "source"));
    Check(source.Contains("location.pathname === \"/player.html\"") && source.Contains("installMeter") && source.Contains("__BILI_RANGE_CORE__"), "the registered script is the guarded bundle with the meter");

    // A service worker says it does not wait, and still has to be released; then Lanes lets go of it.
    fake.Event("Target.attachedToTarget", Attached("worker-1", "service_worker", false));
    await Settle();
    Check(fake.Methods("worker-1").SequenceEqual(new[] { "Runtime.runIfWaitingForDebugger" }), "a worker is released even when it says it does not wait");
    var detach = fake.Sent.LastOrDefault(m => Json.Str(Json.Get(m, "method")) == "Target.detachFromTarget");
    Check(detach != null && Json.Str(Json.Get(Json.Obj(Json.Get(detach, "params")), "sessionId")) == "worker-1", "and then detached");

    // An already open page gets the bundle evaluated once, then the settings.
    fake.Event("Target.attachedToTarget", Attached("page-2", "page", false));
    await Settle();
    var page2 = fake.Methods("page-2");
    Check(page2.Take(2).SequenceEqual(new[] { "Page.enable", "Page.addScriptToEvaluateOnNewDocument" }) && page2.Count(m => m == "Runtime.evaluate") == 2 && !page2.Contains("Runtime.runIfWaitingForDebugger"), "an open page: bundle evaluated once, then the settings pushed: " + string.Join(", ", page2));

    await FragmentedFrames();
  }

  // A minimal WebSocket server that answers one message in three frames.
  static async Task FragmentedFrames() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () => {
      using (var client = await listener.AcceptTcpClientAsync())
      using (var stream = client.GetStream()) {
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length);
        var request = Encoding.ASCII.GetString(buffer, 0, read);
        var key = request.Split(new[] { "\r\n" }, StringSplitOptions.None).First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Substring(18).Trim();
        string accept;
        using (var sha1 = System.Security.Cryptography.SHA1.Create()) accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(head, 0, head.Length);
        var parts = new[] { "{\"id\":1,\"res", "ult\":{\"value\":", "42}}" };
        for (var i = 0; i < parts.Length; i++) {
          var payload = Encoding.UTF8.GetBytes(parts[i]);
          var opcode = i == 0 ? 0x1 : 0x0;
          var fin = i == parts.Length - 1 ? 0x80 : 0x00;
          var frame = new byte[] { (byte)(fin | opcode), (byte)payload.Length }.Concat(payload).ToArray();
          await stream.WriteAsync(frame, 0, frame.Length);
          await Task.Delay(20);
        }
        await Task.Delay(300);
      }
    });
    var transport = await SocketTransport.Connect("ws://127.0.0.1:" + port + "/", 5000);
    var text = await transport.ReceiveAsync();
    transport.Abort();
    listener.Stop();
    Check(text == "{\"id\":1,\"result\":{\"value\":42}}", "a message in three frames is read whole: " + text);
    try { await server; } catch (Exception) { }

    // An endpoint that takes the connection and never answers the handshake: the connect gives up.
    var silent = new TcpListener(IPAddress.Loopback, 0);
    silent.Start();
    var held = silent.AcceptTcpClientAsync();
    var connecting = SocketTransport.Connect("ws://127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port + "/", 300);
    var hung = await Task.WhenAny(connecting, Task.Delay(3000)) == connecting ? await Fails(connecting) : "(still waiting after 3 s)";
    silent.Stop();
    try { (await held).Close(); } catch (Exception) { }
    Check(hung != "(did not fail)" && hung != "(still waiting after 3 s)", "a handshake that never completes times out: " + hung);
  }

  static async Task Polling(string sandbox) {
    var controller = new Controller(sandbox, s => { }, new FakeSystem());
    var fake = new FakeTransport();
    await controller.Attach(new Cdp(fake));
    await controller.Poll();
    var empty = controller.Status();
    Check(Json.Num(empty["speed"]) == 0 && Json.Arr(empty["threadSpeeds"]).Length == 0 && !Json.Bool(empty["playing"]), "no pages: nothing to show");
    // A worker-only session adds no page.
    fake.Event("Target.attachedToTarget", Attached("worker-2", "shared_worker", false));
    await Settle();
    await controller.Poll();
    Check(Json.Arr(controller.Status()["threadSpeeds"]).Length == 0, "a worker is not polled as a page");
    // One page with one slot: baseline first, then growth.
    var bytes = 0;
    fake.EvaluateAnswer = m => {
      var expression = Json.Str(Json.Get(Json.Obj(Json.Get(m, "params")), "expression"));
      if (!expression.Contains("local.lease = Date.now()")) return 0;
      return new Dictionary<string, object> {
        { "playing", true }, { "bytes", bytes }, { "requests", 3 }, { "fallbacks", 0 }, { "active", 1 }, { "threads", 8 }, { "suspended", false }, { "page", 7.5 },
        { "settings", new Dictionary<string, object> { { "enabled", true }, { "mode", "custom" }, { "autoConcurrency", true }, { "concurrency", 8 }, { "hostsOk", true } } },
        { "slots", new object[] { new object[] { bytes, "a.bilivideo.com", 1 } } }
      };
    };
    fake.Event("Target.attachedToTarget", Attached("page-3", "page", false));
    await Settle();
    await controller.Poll();
    bytes = 1000000;
    await Task.Delay(200);
    await controller.Poll();
    var status = controller.Status();
    var slots = Json.Arr(status["threadSpeeds"]);
    Check(slots.Length == 1 && Json.Num(Json.Get(Json.Obj(slots[0]), "speed")) > 0 && Json.Str(Json.Get(Json.Obj(slots[0]), "host")) == "a.bilivideo.com", "one slot shows its speed and node");
    Check(Json.Num(status["speed"]) > 0 && Json.Bool(status["playing"]) && Json.Num(status["threads"]) == 8, "the page's speed and state");
  }

  // ---- client processes and restarts, against a fake process table ---------------------------------------------------
  sealed class FakeSystem : IClientSystem {
    public readonly List<ProcessInfo> Table = new List<ProcessInfo>();
    public readonly Dictionary<int, string> Paths = new Dictionary<int, string>();
    public readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Registry = new Dictionary<string, string>();
    public readonly List<int> Killed = new List<int>();
    public readonly List<string> Launched = new List<string>();
    public Action BeforeStopSnapshot; // lets a check change the table between identification and the stop
    int snapshots;

    public readonly HashSet<string> Holders = new HashSet<string>(); // id@start of the browser processes (AddRun)
    public bool UsersUnknown; // the Restart Manager query fails
    public bool LockfileBusy() { lock (Table) return Table.Any(p => p.Name == Client.ExeName); }
    public List<ProcessInfo> LockfileUsers() {
      if (UsersUnknown) return null;
      lock (Table) return Table.Where(p => Holders.Contains(p.Id + "@" + p.StartedMs)).Select(p => new ProcessInfo { Id = p.Id, StartedMs = p.StartedMs }).ToList();
    }
    public long LockfileStamp() { return 1; }
    public List<ProcessInfo> Snapshot() {
      lock (Table) {
        snapshots++;
        if (snapshots == 2 && BeforeStopSnapshot != null) BeforeStopSnapshot();
        return Table.Select(p => new ProcessInfo { Id = p.Id, ParentId = p.ParentId, Name = p.Name, StartedMs = p.StartedMs }).ToList();
      }
    }
    public string ExecutablePath(int id) { string path; return Paths.TryGetValue(id, out path) ? path : ""; }
    public bool FileExists(string path) { return Files.Contains(path); }
    public bool Exits, Denied; // the client exits by itself just before the kill / cannot be ended (elevated)
    public bool Kill(int id, long startedMs) {
      lock (Table) {
        if (Denied) return false;
        if (Exits) { Table.RemoveAll(p => p.Name == Client.ExeName); return false; }
        var process = Table.FirstOrDefault(p => p.Id == id && p.StartedMs == startedMs);
        if (process == null) return false;
        Table.Remove(process); Killed.Add(id); return true;
      }
    }
    public void Launch(string path, string arguments) { Launched.Add(path + " " + arguments); }
    public string RegistryValue(string root, string key, string name) { string value; return Registry.TryGetValue(root + "\\" + key + "\\" + name, out value) ? value : null; }

    public void AddRun(int id, long started, string path) {
      Table.Add(new ProcessInfo { Id = id, ParentId = 4, Name = Client.ExeName, StartedMs = started });
      Holders.Add(id + "@" + started);
      Table.Add(new ProcessInfo { Id = id + 1, ParentId = id, Name = Client.ExeName, StartedMs = started + 500 });
      Table.Add(new ProcessInfo { Id = id + 2, ParentId = id, Name = Client.ExeName, StartedMs = started + 600 });
      if (path != null) Paths[id] = path;
    }
  }

  static void ClientProcesses() {
    var exe = @"D:\Portable\bilibili\" + Client.ExeName;
    var system = new FakeSystem();
    system.Files.Add(exe);
    system.AddRun(100, 5000, exe);
    system.Table.Add(new ProcessInfo { Id = 900, ParentId = 4, Name = "other.exe", StartedMs = 0 });
    var run = Client.Identify(system);
    Check(run != null && run.Id == 100 && run.StartedMs == 5000 && run.Path == exe, "the earliest client process is the run, with its path");
    Check(Client.RunProcesses(run, system.Snapshot()).Select(p => p.Id).OrderBy(i => i).SequenceEqual(new[] { 100, 101, 102 }), "the run's processes are its browser process and children");

    // The run exits and a new one starts between identification and the stop: nothing of the new run is touched.
    var racing = new FakeSystem();
    racing.Files.Add(exe);
    racing.AddRun(100, 5000, exe);
    var captured = Client.Identify(racing);
    racing.Table.Clear();
    racing.AddRun(300, 9000, exe);
    Check(!Client.Stop(racing, captured) && racing.Killed.Count == 0, "a run that appeared after the check is never stopped");
    // The same process id now belongs to another start time.
    var reused = new FakeSystem();
    reused.AddRun(100, 5000, exe);
    var before = Client.Identify(reused);
    reused.Table.Clear();
    reused.AddRun(100, 7000, exe);
    Check(!Client.Stop(reused, before) && reused.Killed.Count == 0, "a reused process id is not the same run");

    // A child of a run that ended abnormally can outlive it and is older than the next run: the run is the lockfile's
    // holder, and stopping it leaves the leftover alone. Nothing is guessed when the holder cannot be read.
    var leftover = new FakeSystem();
    leftover.Files.Add(exe);
    leftover.Table.Add(new ProcessInfo { Id = 50, ParentId = 999, Name = Client.ExeName, StartedMs = 1000 });
    leftover.AddRun(100, 5000, exe);
    var current = Client.Identify(leftover);
    Check(current != null && current.Id == 100 && current.StartedMs == 5000, "the run is the lockfile's holder, not an older leftover child");
    Check(Client.Stop(leftover, current) && leftover.Killed.OrderBy(i => i).SequenceEqual(new[] { 100, 101, 102 }), "stopping the run leaves the leftover alone");
    var unreadable = new FakeSystem { UsersUnknown = true };
    unreadable.AddRun(100, 5000, exe);
    Check(Client.Identify(unreadable) == null, "no run is guessed when the lockfile's holder cannot be read");

    // The real Restart Manager query: it names this process while it holds a file, with its start time; nobody after.
    var held = Path.Combine(Path.GetTempPath(), "lanes-check-" + Guid.NewGuid().ToString("N") + ".lock");
    try {
      using (new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
        var self = Process.GetCurrentProcess();
        var started = (self.StartTime.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerMillisecond;
        var users = WindowsClientSystem.FileUsers(held);
        Check(users != null && users.Any(u => u.Id == self.Id && u.StartedMs == started), "Restart Manager names the process holding a file, with its start time");
      }
      var free = WindowsClientSystem.FileUsers(held);
      Check(free != null && free.Count == 0, "Restart Manager names nobody once the file is closed");
    } finally { File.Delete(held); }

    // The real kill, on a real process: one with another start time is left alone; the matching one ends.
    using (var sleeper = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })) {
      try {
        var started = (sleeper.StartTime.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerMillisecond;
        var windows = new WindowsClientSystem();
        Check(!windows.Kill(sleeper.Id, started + 1) && !sleeper.WaitForExit(300), "the real kill leaves a process with another start time alone");
        Check(windows.Kill(sleeper.Id, started) && sleeper.WaitForExit(3000), "the real kill ends the process it was given");
      } finally { if (!sleeper.HasExited) sleeper.Kill(); }
    }

    // The uninstall record, as before: HKCU then HKLM, normal and WOW6432Node keys, InstallLocation then DisplayIcon.
    var registry = new FakeSystem();
    const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili";
    const string wow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili";
    registry.Registry[@"HKLM\" + key + @"\InstallLocation"] = "";
    registry.Registry[@"HKLM\" + key + @"\DisplayIcon"] = @"C:\Program Files\bilibili\uninstallerIcon.ico";
    Check(Client.FromUninstallRecord(registry) == null, "an install whose executable is missing is not used");
    registry.Files.Add(@"C:\Program Files\bilibili\" + Client.ExeName);
    Check(Client.FromUninstallRecord(registry) == @"C:\Program Files\bilibili\" + Client.ExeName, "the folder of DisplayIcon");
    registry.Registry[@"HKCU\" + wow + @"\InstallLocation"] = @"E:\Bili";
    registry.Files.Add(@"E:\Bili\" + Client.ExeName);
    Check(Client.FromUninstallRecord(registry) == @"E:\Bili\" + Client.ExeName, "HKCU comes before HKLM");
    foreach (var icon in new[] { @"E:\Bili,Portable\uninstallerIcon.ico", "\"E:\\Bili,Portable\\uninstaller.exe\",0", @"E:\Bili,Portable\uninstaller.exe,0" }) {
      var comma = new FakeSystem();
      comma.Registry[@"HKLM\" + key + @"\DisplayIcon"] = icon;
      comma.Files.Add(@"E:\Bili,Portable\" + Client.ExeName);
      Check(Client.FromUninstallRecord(comma) == @"E:\Bili,Portable\" + Client.ExeName, "an install folder with a comma, from DisplayIcon: " + icon);
    }
    var relative = new FakeSystem();
    relative.Registry[@"HKCU\" + key + @"\InstallLocation"] = "D:Apps";
    relative.Files.Add(@"D:Apps\" + Client.ExeName);
    Check(Client.FromUninstallRecord(relative) == null, "an install folder relative to the current directory is not used");
  }

  static async Task Restarts(string sandbox) {
    var exe = @"D:\Portable\bilibili\" + Client.ExeName;
    var stale = @"C:\Program Files\bilibili\" + Client.ExeName;

    // The running client's own executable wins over a stale uninstall record.
    var system = new FakeSystem();
    system.Files.Add(exe); system.Files.Add(stale);
    system.Registry[@"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili\InstallLocation"] = @"C:\Program Files\bilibili";
    system.AddRun(100, 5000, exe);
    var controller = new Controller(sandbox, s => { }, system);
    await controller.RestartClient(null, null);
    Check(system.Killed.OrderBy(i => i).SequenceEqual(new[] { 100, 101, 102 }) && system.Launched.SequenceEqual(new[] { exe + " --remote-debugging-port=39229" }), "a restart launches exactly the running client's path: " + string.Join("; ", system.Launched));

    // No readable path: nothing is stopped; the user is asked.
    var unknown = new FakeSystem();
    unknown.AddRun(100, 5000, null);
    var asking = new Controller(sandbox, s => { }, unknown);
    await asking.RestartClient(null, null);
    Check(unknown.Killed.Count == 0 && unknown.Launched.Count == 0 && asking.State == "client-path-missing", "no path: no stop, and the user is asked");
    // The user picks the executable: it is checked, remembered and used.
    unknown.Files.Add(exe);
    Check(asking.ChooseClient(exe), "a valid pick is taken");
    await Settle();
    Check(unknown.Killed.Count == 3 && unknown.Launched.SequenceEqual(new[] { exe + " --remote-debugging-port=39229" }) && Json.Str(asking.Settings["clientExe"]) == exe, "the picked executable restarts the client and is remembered");
    Check(!asking.ChooseClient(@"D:\elsewhere\notepad.exe"), "a wrong pick is refused");

    // An automatic takeover without a path stops nothing and does not use up a try.
    var takeover = new FakeSystem();
    takeover.AddRun(100, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds - 1000, null);
    var auto = new Controller(sandbox, s => { }, takeover);
    var client = Client.Identify(takeover);
    var nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    Check(!await auto.TakeoverStep(client, nowMs) && auto.TakeoverTriesUsed == 0 && takeover.Killed.Count == 0 && auto.State == "client-path-missing", "a takeover without a path does not count");

    // The run changes between the final identity check and the stop: only the captured run may be stopped.
    var race = new FakeSystem();
    race.Files.Add(exe);
    race.AddRun(100, 5000, exe);
    race.BeforeStopSnapshot = () => { race.Table.Clear(); race.AddRun(300, 9000, exe); };
    var careful = new Controller(sandbox, s => { }, race);
    await careful.RestartClient(null, null);
    Check(race.Killed.Count == 0 && race.Launched.Count == 0, "a run that replaced the checked one between the check and the stop is left alone");

    // "Open Bilibili": the remembered executable, then the uninstall record; nothing known: ask.
    var closed = new FakeSystem();
    var opener = new Controller(sandbox, s => { }, closed);
    opener.ApplySettings(new Dictionary<string, object> { { "clientExe", "" } });
    opener.Launch();
    Check(closed.Launched.Count == 0 && opener.State == "client-path-missing", "no known executable: the user is asked");
    closed.Files.Add(exe);
    opener.ApplySettings(new Dictionary<string, object> { { "clientExe", exe } });
    opener.Launch();
    Check(closed.Launched.SequenceEqual(new[] { exe + " --remote-debugging-port=39229" }), "the remembered executable is opened");

    // The user closes the client while Lanes restarts it: it is not opened again. One that cannot be ended (elevated)
    // still counts as stopped, so the wait for the lockfile reports that it could not be closed.
    var closing = new FakeSystem { Exits = true };
    closing.Files.Add(exe);
    closing.AddRun(100, 5000, exe);
    var closer = new Controller(sandbox, s => { }, closing);
    await closer.RestartClient(null, null);
    Check(closing.Launched.Count == 0 && closer.State == "client-off", "a client closed during the restart is not opened again: " + closer.State + ", " + closing.Launched.Count + " launched");
    var elevated = new FakeSystem { Denied = true };
    elevated.AddRun(100, 5000, exe);
    Check(Client.Stop(elevated, Client.Identify(elevated)), "a client that cannot be ended counts as stopped");
  }
}
