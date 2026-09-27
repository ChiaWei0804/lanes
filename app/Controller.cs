// The controller: runs Bilibili-thread-ripper's page code (vendor/btr, unmodified) inside the official Bilibili client
// without touching the client's files. The client runs with a loopback CDP port; the bundle is injected into its
// player page; this class owns the settings, the takeover of clients opened without the port, and updates.
// Everything here runs on the WPF dispatcher thread with async/await, like a single event loop; blocking work goes to
// Task.Run and comes back with await.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

public sealed class Controller {
  public const int CdpPort = 39229;
  const int LeaseMs = 6000, PollMs = 500;
  // A client opened while Lanes runs is restarted with the port while it is this fresh (nothing plays yet).
  // Automatic restarts are spaced out, and stop after two in a row that did not end in a connection.
  public const int TakeoverAgeMs = 15000, TakeoverGapMs = 10000, TakeoverTries = 2;
  static readonly object[] Threads = { "auto", 8, 16, 32, 64 };
  static readonly string[] Modes = { "auto", "overseas", "mainland" }, Languages = { "en", "zh-Hant", "zh-Hans" };
  // BTR's page files in its own build order (tools/build.cjs), without its in-client settings UI and updater.
  static readonly string[] BtrFiles = { "range-core.js", "cdn-resolver.js", "idm-downloader.js", "runtime-notices.js", "notification-view.js", "settings.js", "transport.js", "client.js" };

  readonly string folder;
  readonly Action<string> log;
  readonly IClientSystem system;
  public readonly string Version, Repository;
  public Dictionary<string, object> Settings;
  string pageScript, pollScript;

  public Controller(string folder, Action<string> log, IClientSystem system) {
    this.folder = folder;
    this.log = log;
    this.system = system;
    var version = Json.Obj(Json.ReadFile(Path.Combine(this.folder, "version.json")));
    Version = Json.Str(Json.Get(version, "version"));
    Repository = Json.Str(Json.Get(version, "repository"));
    Settings = Defaults();
    try { var saved = Json.Obj(Json.ReadFile(SettingsFile)); if (saved != null) Settings = Merged(Defaults(), saved); } catch (Exception) { }
  }

  string SettingsFile { get { return Path.Combine(folder, "settings.json"); } }

  // ---- settings ----------------------------------------------------------------------------------------------------
  public static Dictionary<string, object> Defaults() {
    return new Dictionary<string, object> { { "enabled", true }, { "threads", "auto" }, { "mode", "auto" }, { "closeToTray", false }, { "restartRunningClient", true }, { "language", "en" }, { "clientExe", "" } };
  }

  // Only known values of the right type are taken, from the window and from a hand-edited settings.json alike.
  public static Dictionary<string, object> Merged(Dictionary<string, object> current, Dictionary<string, object> patch) {
    var next = new Dictionary<string, object>(current);
    foreach (var key in new[] { "enabled", "closeToTray", "restartRunningClient" }) if (Json.Get(patch, key) is bool) next[key] = patch[key];
    var threads = Json.Get(patch, "threads");
    foreach (var allowed in Threads) if (Json.Same(allowed, threads) && (threads is string) == (allowed is string)) next["threads"] = allowed;
    if (Modes.Contains(Json.Get(patch, "mode") as string)) next["mode"] = patch["mode"];
    if (Languages.Contains(Json.Get(patch, "language") as string)) next["language"] = patch["language"];
    // The client's executable: an absolute path with the client's file name, or "" (checked for existence when used).
    var exe = Json.Get(patch, "clientExe") as string;
    if (exe != null && (exe.Length == 0 || Client.IsClientPath(exe))) next["clientExe"] = exe;
    return next;
  }

  public void ApplySettings(Dictionary<string, object> patch) {
    Settings = Merged(Settings, patch);
    try { File.WriteAllText(SettingsFile, Json.WriteFlat(Settings), new UTF8Encoding(false)); }
    catch (Exception e) { log("Could not save settings.json: " + e.Message); }
    foreach (var sessionId in pages.Keys.ToList()) PushSettings(sessionId);
  }

  // Region "auto" is BTR's custom mode over all of its nodes, overseas and mainland: BTR measures each node and gives
  // the fast ones the video. The node list is BTR's own GLOBAL_HOSTS, filled in by the page (PushScript).
  public static Dictionary<string, object> ToBtr(Dictionary<string, object> s) {
    var btr = new Dictionary<string, object> { { "enabled", s["enabled"] }, { "mode", (string)s["mode"] == "auto" ? "custom" : s["mode"] } };
    if (s["threads"] is string) btr["autoConcurrency"] = true;
    else { btr["autoConcurrency"] = false; btr["concurrency"] = s["threads"]; }
    return btr;
  }

  // BTR's settings in a page differ from this program's: a new page starts from BTR's stored ones, and a lease may have
  // run out while this program was busy.
  public static bool NeedsPush(Dictionary<string, object> page, Dictionary<string, object> want) {
    if (page == null || !Json.Bool(Json.Get(page, "hostsOk"))) return true;
    return want.Any(pair => !Json.Same(Json.Get(page, pair.Key), pair.Value));
  }

  public static string PushScript(Dictionary<string, object> s) {
    return "(() => {\n" +
      "  if (!globalThis.__BTR_LOCAL__ || !globalThis.__BTR_DESKTOP__) return 0;\n" +
      "  const want = " + Json.Write(ToBtr(s)) + ";\n" +
      "  if (want.mode === \"custom\") want.customHosts = globalThis.__BILI_CDN_RESOLVER_FACTORY__.GLOBAL_HOSTS.slice();\n" +
      "  globalThis.__BTR_DESKTOP__.setSettings(want);\n" +
      "  return 0;\n" +
      "})()";
  }

  // ---- page scripts (JavaScript that runs inside the player page) ------------------------------------------------
  public static string Resource(string name) {
    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
    using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
  }

  public string PageScript() {
    if (pageScript == null) {
      var bundle = string.Join("\n;\n", BtrFiles.Select(file => File.ReadAllText(Path.Combine(folder, "vendor", "btr", file), Encoding.UTF8)));
      pageScript = Resource("page.inject.js").Replace("{{version}}", Version).Replace("{{meter}}", Resource("page.meter.js").Trim()).Replace("{{bundle}}", bundle);
    }
    // The settings in effect when the page is registered; a page lives on for every video its window plays.
    return pageScript.Replace("{{settings}}", PushScript(Settings));
  }

  public string PollScript() { return pollScript ?? (pollScript = Resource("page.poll.js")); }

  // ---- CDP ----------------------------------------------------------------------------------------------------------
  Cdp cdp;
  public string State = "connecting";
  public sealed class PageMemo {
    public readonly Dictionary<string, double> Counters = new Dictionary<string, double>();
    public object Id = Unset;
    public List<SlotMemo> Slots = new List<SlotMemo>();
  }
  static readonly object Unset = new object();
  readonly Dictionary<string, PageMemo> pages = new Dictionary<string, PageMemo>();
  public bool Connected { get { return cdp != null && !cdp.Closed; } }

  static readonly Dictionary<string, string> StateNames = new Dictionary<string, string> {
    { "connected", "connected to Bilibili" }, { "client-off", "Bilibili is not open" }, { "needs-restart", "Bilibili runs without the debugging port" },
    { "starting", "starting Bilibili" }, { "restart-failed", "restarting Bilibili failed" }, { "client-path-missing", "the Bilibili executable is not known" }
  };
  void SetState(string next) {
    if (next == State) return;
    State = next;
    string name;
    log("State: " + (StateNames.TryGetValue(next, out name) ? name : next));
  }

  static void Ignore(Task task) { task.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }

  async Task Connect() {
    string url;
    using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) }) {
      url = Json.Str(Json.Get(Json.Obj(Json.Read(await http.GetStringAsync("http://127.0.0.1:" + CdpPort + "/json/version"))), "webSocketDebuggerUrl"));
    }
    var transport = await SocketTransport.Connect(url, 5000);
    await Attach(new Cdp(transport));
  }

  // Also used by the self-check with a fake transport.
  public async Task Attach(Cdp connection) {
    cdp = connection;
    connection.OnEvent = OnEvent;
    connection.OnClosed = () => {
      if (cdp != connection) return;
      cdp = null;
      pages.Clear();
      if (quitting) return;
      log("Lost the connection to Bilibili");
      SetState("client-off");
      WatchClient();
    };
    connection.Start();
    // Every new target waits for this program (no filter: all but the browser itself), because the wait also holds
    // targets a filter would leave out: a service worker held that way never starts, and live.bilibili.com's pages,
    // which all go through one, stay black. Pages get the bundle; everything else is released at once (ReleaseTarget).
    try {
      await connection.Send("Target.setAutoAttach", new Dictionary<string, object> { { "autoAttach", true }, { "waitForDebuggerOnStart", true }, { "flatten", true } }, null, 5000);
    } catch (Exception) {
      // Without auto-attach nothing gets the bundle: drop the connection so the watch loop tries again.
      connection.Abort();
      throw;
    }
    takeoverTries = 0;
    SetState("connected");
  }

  void OnEvent(string method, Dictionary<string, object> message) {
    var parameters = Json.Obj(Json.Get(message, "params"));
    if (method == "Target.attachedToTarget") {
      var sessionId = Json.Str(Json.Get(parameters, "sessionId"));
      var type = Json.Str(Json.Get(Json.Obj(Json.Get(parameters, "targetInfo")), "type"));
      if (type == "page") AttachPage(sessionId, Json.Bool(Json.Get(parameters, "waitingForDebugger")));
      else ReleaseTarget(sessionId);
    } else if (method == "Target.detachedFromTarget") {
      pages.Remove(Json.Str(Json.Get(parameters, "sessionId")));
    }
  }

  async void AttachPage(string sessionId, bool waitingForDebugger) {
    var connection = cdp;
    pages[sessionId] = new PageMemo();
    // Chrome 108 ignores new-document scripts until the Page domain is enabled.
    Ignore(connection.Send("Page.enable", null, sessionId, 0));
    var registered = connection.Send("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source", PageScript() } }, sessionId, 0);
    Ignore(registered);
    // A new page stays paused, answering nothing, until it is released — awaiting the registration first deadlocks
    // it (black player). A session runs its commands in order, so queuing all three at once still puts the bundle in
    // place before the page's own scripts; the poll then pushes this program's settings.
    if (waitingForDebugger) { Ignore(connection.Send("Runtime.runIfWaitingForDebugger", null, sessionId, 0)); return; }
    // A page that was already open when this program started gets the bundle now.
    try {
      await registered;
      await connection.Evaluate(sessionId, PageScript() + ";0", 10000);
      await PushSettingsAsync(sessionId);
    } catch (Exception) { }
  }

  // A worker or any other target that is not a page: let it run and let go of it. A service worker does not always say
  // that it waits (waitingForDebugger false) and still holds its start until released, so it is released regardless.
  async void ReleaseTarget(string sessionId) {
    var connection = cdp;
    try { await connection.Send("Runtime.runIfWaitingForDebugger", null, sessionId, 5000); } catch (Exception) { }
    try { await connection.Send("Target.detachFromTarget", new Dictionary<string, object> { { "sessionId", sessionId } }, null, 5000); } catch (Exception) { }
  }

  void PushSettings(string sessionId) { Ignore(PushSettingsAsync(sessionId)); }
  Task PushSettingsAsync(string sessionId) {
    var connection = cdp;
    return connection == null ? Task.FromResult<object>(null) : connection.Evaluate(sessionId, PushScript(Settings), 3000);
  }

  // ---- status and speed -------------------------------------------------------------------------------------------
  double speed;
  DateTime lastPoll = DateTime.UtcNow;
  bool polling;
  Dictionary<string, object> stats = EmptyStats();
  // BTR's counters run from the page's load, which may predate this run of Lanes; only growth seen since Lanes started
  // counts, and a reloaded page (counter back near zero) just sets a new baseline.
  double countedRequests, countedFallbacks;
  sealed class ThreadSpeed { public double Speed; public string Host = ""; }
  List<ThreadSpeed> threadSpeeds = new List<ThreadSpeed>();

  static Dictionary<string, object> EmptyStats() {
    return new Dictionary<string, object> { { "requests", 0.0 }, { "fallbacks", 0.0 }, { "active", 0.0 }, { "threads", 0.0 }, { "suspended", false }, { "playing", false }, { "foreign", false } };
  }

  static double Grown(Dictionary<string, double> memo, string key, double value) {
    double before;
    var seen = memo.TryGetValue(key, out before);
    memo[key] = value;
    return !seen || value < before ? 0 : value - before;
  }

  public sealed class SlotMemo { public bool Seen; public double Bytes; public object Run; }
  public sealed class SlotStepResult { public double Rate; public bool Restart; public string Host; }

  // One poll of one slot: its cell's bytes/s, and whether the cell's smoothed speed starts over. A slot seen for the
  // first time only sets its baseline; a new run (the slot moved to another node) counts only that run's bytes. A page
  // injected by an older Lanes reports no run, and keeps counting as one run.
  public static SlotStepResult SlotStep(SlotMemo memo, object[] sample, double seconds) {
    var bytes = Json.Num(sample.Length > 0 ? sample[0] : null);
    var host = sample.Length > 1 ? Json.Str(sample[1]) : "";
    var run = sample.Length > 2 ? sample[2] : null;
    var sameRun = memo.Seen && Json.Same(memo.Run, run);
    var delta = !memo.Seen ? 0 : sameRun ? Math.Max(0, bytes - memo.Bytes) : bytes;
    memo.Seen = true; memo.Bytes = bytes; memo.Run = run;
    return new SlotStepResult { Rate = delta / seconds, Restart = !sameRun, Host = host };
  }

  // A reload keeps the CDP session but brings a new meter (a new id), whose slots start again. A page injected by an
  // older Lanes has no id.
  public static List<SlotMemo> SlotMemos(PageMemo page, object id) {
    if (!Json.Same(page.Id, id)) { page.Id = id; page.Slots = new List<SlotMemo>(); }
    return page.Slots;
  }

  public async Task Poll() {
    var connection = cdp;
    if (polling || connection == null) return;
    polling = true;
    try {
      var now = DateTime.UtcNow;
      var seconds = Math.Max(0.001, (now - lastPoll).TotalSeconds);
      lastPoll = now;
      double bytes = 0;
      var next = EmptyStats();
      var slotRates = new List<double>();
      var slotHosts = new List<string>();
      var slotRestarts = new List<bool>();
      var want = ToBtr(Settings);
      var polls = pages.ToList().Select(async pair => {
        object value = null;
        try { value = await connection.Evaluate(pair.Key, PollScript(), 3000); } catch (Exception) { }
        return new KeyValuePair<string, Dictionary<string, object>>(pair.Key, Json.Obj(value));
      }).ToList();
      foreach (var result in await Task.WhenAll(polls)) {
        var s = result.Value;
        PageMemo page;
        if (s == null || !pages.TryGetValue(result.Key, out page)) continue;
        if (Json.Bool(Json.Get(s, "foreign"))) { next["foreign"] = true; continue; }
        bytes += Grown(page.Counters, "bytes", Json.Num(Json.Get(s, "bytes")));
        var memos = SlotMemos(page, Json.Get(s, "page"));
        var samples = Json.Arr(Json.Get(s, "slots")) ?? new object[0];
        for (var i = 0; i < samples.Length; i++) {
          while (memos.Count <= i) memos.Add(new SlotMemo());
          while (slotRates.Count <= i) { slotRates.Add(0); slotHosts.Add(""); slotRestarts.Add(false); }
          var step = SlotStep(memos[i], Json.Arr(samples[i]) ?? new object[0], seconds);
          slotRates[i] += step.Rate;
          if (step.Restart) slotRestarts[i] = true;
          if (step.Host.Length > 0) slotHosts[i] = step.Host;
        }
        countedRequests += Grown(page.Counters, "requests", Json.Num(Json.Get(s, "requests")));
        countedFallbacks += Grown(page.Counters, "fallbacks", Json.Num(Json.Get(s, "fallbacks")));
        if (NeedsPush(Json.Obj(Json.Get(s, "settings")), want)) PushSettings(result.Key);
        if (Json.Bool(Json.Get(s, "playing"))) next["playing"] = true;
        next["active"] = Json.Num(next["active"]) + Json.Num(Json.Get(s, "active"));
        next["threads"] = Math.Max(Json.Num(next["threads"]), Json.Num(Json.Get(s, "threads")));
        if (Json.Bool(Json.Get(s, "suspended"))) next["suspended"] = true;
      }
      speed = speed * 0.5 + bytes / seconds * 0.5;
      next["requests"] = countedRequests;
      next["fallbacks"] = countedFallbacks;
      var speeds = new List<ThreadSpeed>();
      for (var i = 0; i < slotRates.Count; i++) {
        var previous = i < threadSpeeds.Count ? threadSpeeds[i] : null;
        speeds.Add(new ThreadSpeed {
          Speed = slotRestarts[i] ? slotRates[i] : (previous != null ? previous.Speed : 0) * 0.5 + slotRates[i] * 0.5,
          Host = slotHosts[i].Length > 0 ? slotHosts[i] : previous != null ? previous.Host : ""
        });
      }
      threadSpeeds = speeds;
      if (Json.Num(next["fallbacks"]) > Json.Num(stats["fallbacks"])) log("Acceleration failed and a request went back to native download (" + next["fallbacks"] + " so far)");
      if (Json.Bool(next["suspended"]) && !Json.Bool(stats["suspended"])) log("A player window kept failing; BTR suspended acceleration there");
      if (Json.Bool(next["foreign"]) && !Json.Bool(stats["foreign"])) log("BTR Desktop is installed in the client; Lanes does not inject there");
      stats = next;
    } finally {
      polling = false;
    }
  }

  // What the window shows, in one snapshot.
  public Dictionary<string, object> Status() {
    var status = new Dictionary<string, object>(stats) {
      { "state", State }, { "settings", new Dictionary<string, object>(Settings) }, { "version", Version }, { "speed", Math.Round(speed) },
      { "update", new Dictionary<string, object> { { "state", update.State }, { "latest", update.Latest ?? "" }, { "progress", update.Progress } } },
      { "threadSpeeds", threadSpeeds.Select(t => (object)new Dictionary<string, object> { { "speed", Math.Round(t.Speed) }, { "host", t.Host } }).ToArray() }
    };
    return status;
  }

  // ---- client process -----------------------------------------------------------------------------------------------
  // Whether to restart a running client (started without the port) so it can be accelerated: one that appeared while
  // Lanes runs and is still fresh; or, with "connect to a Bilibili that is already open" on, any other one. Each client
  // run is tried once, restarts are spaced out, and two in a row that did not connect stop them.
  public static bool ShouldTakeOver(ClientRun client, string initial, HashSet<string> attempted, double lastTakeoverAt, double now, bool restartRunning, int tries) {
    if (client == null || attempted.Contains(client.Key) || now - lastTakeoverAt < TakeoverGapMs || tries >= TakeoverTries) return false;
    return restartRunning || (client.Key != initial && now - client.StartedMs < TakeoverAgeMs);
  }

  static double NowMs() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

  double startedAt, lastTakeoverAt = double.NegativeInfinity;
  bool restarting, watching, quitting;
  string initialClient;
  int takeoverTries;
  readonly HashSet<string> attempted = new HashSet<string>();
  string pendingChoice; // "launch" or "restart": what a chosen executable is for

  // The lockfile changes whenever the client starts again, so the process query runs once per client run.
  long identityStamp = -1;
  ClientRun identity;
  async Task<ClientRun> CurrentClient() {
    var stamp = system.LockfileStamp();
    // A query that found no client is not kept: the next one may (a client just starting).
    if (stamp != identityStamp || identity == null) {
      identity = await Task.Run(() => Client.Identify(system));
      identityStamp = stamp;
      Remember(identity);
    }
    return identity;
  }

  // The executable of a client that was seen running: "Open Bilibili" uses it once the client is closed.
  void Remember(ClientRun run) {
    if (run != null && run.Path.Length > 0 && !string.Equals(run.Path, Json.Str(Settings["clientExe"]), StringComparison.OrdinalIgnoreCase))
      ApplySettings(new Dictionary<string, object> { { "clientExe", run.Path } });
  }

  // "Open Bilibili" while no client runs: the remembered executable, the uninstall record, the default location, or
  // else ask the user.
  public void Launch() {
    if (restarting) return;
    var candidates = new[] { Json.Str(Settings["clientExe"]), Client.FromUninstallRecord(system), Client.DefaultExe };
    var path = candidates.FirstOrDefault(p => Client.Launchable(system, p));
    if (path == null) { pendingChoice = "launch"; log("No Bilibili executable found to open"); SetState("client-path-missing"); return; }
    StartClient(path);
  }

  void StartClient(string path) {
    log("Opening Bilibili with the debugging port: " + path);
    try { system.Launch(path, "--remote-debugging-port=" + CdpPort); }
    catch (Exception e) { log("Could not open Bilibili (" + path + "): " + e.Message); SetState("client-off"); return; }
    startedAt = NowMs();
    SetState("starting");
  }

  // The notice's restart button. Restarts only the run that is there now, from its own executable.
  public void Restart() { Ignore(RestartClient(null, null)); }

  // expectedKey: restart only that client run (an automatic takeover). path: an executable the user chose.
  // Returns false when nothing was stopped because no executable is known.
  public async Task<bool> RestartClient(string expectedKey, string chosenPath) {
    if (restarting) return true;
    restarting = true;
    try {
      var run = await Task.Run(() => Client.Identify(system));
      if (run == null) { log("Bilibili is not running; nothing to restart"); return true; }
      if (expectedKey != null && run.Key != expectedKey) { log("The client to take over is no longer the same run; not restarting it"); return true; }
      // The running client's own executable, or one the user just chose; never a guess that may name another copy.
      var path = chosenPath ?? run.Path;
      if (!Client.Launchable(system, path)) {
        pendingChoice = "restart";
        log("Could not tell where the running Bilibili is installed; not restarting it");
        SetState("client-path-missing");
        return false;
      }
      Remember(run);
      log(expectedKey != null ? "Restarting Bilibili automatically so it can be accelerated" : "Restarting Bilibili");
      SetState("starting");
      startedAt = NowMs();
      if (!await Task.Run(() => Client.Stop(system, run))) {
        log("Bilibili closed or changed before it could be stopped; not restarting it");
        SetState(system.LockfileBusy() ? "needs-restart" : "client-off");
        return true;
      }
      // The client is single-instance: a launch while an old process lingers is swallowed by that process.
      for (var i = 0; i < 40; i++) {
        if (!system.LockfileBusy()) { restarting = false; StartClient(path); return true; }
        await Task.Delay(250);
      }
      log("Could not close Bilibili; restart failed");
      SetState("restart-failed");
      return true;
    } finally {
      restarting = false;
    }
  }

  // The executable the user picked in the notice: checked, remembered, then used for what was waiting on it.
  public bool ChooseClient(string path) {
    if (!Client.Launchable(system, path)) { log("The chosen file is not the Bilibili executable: " + path); return false; }
    ApplySettings(new Dictionary<string, object> { { "clientExe", Path.GetFullPath(path) } });
    log("Bilibili executable chosen: " + path);
    if (system.LockfileBusy()) Ignore(RestartClient(null, Path.GetFullPath(path)));
    else StartClient(Path.GetFullPath(path));
    pendingChoice = null;
    return true;
  }

  public int TakeoverTriesUsed { get { return takeoverTries; } }

  // One look at a client that runs without the port: restart it when the rules say so. A takeover that cannot find the
  // executable stops nothing and does not count as a try. True when a restart was made.
  public async Task<bool> TakeoverStep(ClientRun client, double now) {
    if (ShouldTakeOver(client, initialClient, attempted, lastTakeoverAt, now, Json.Bool(Settings["restartRunningClient"]), takeoverTries)) {
      if (!Client.Launchable(system, client.Path)) {
        if (State != "client-path-missing") { pendingChoice = "restart"; log("Could not tell where the running Bilibili is installed; not restarting it"); SetState("client-path-missing"); }
        return false;
      }
      attempted.Add(client.Key);
      lastTakeoverAt = now;
      takeoverTries++;
      await RestartClient(client.Key, null);
      return true;
    }
    if (State != "restart-failed" && State != "client-path-missing") SetState("needs-restart");
    return false;
  }

  async void WatchClient() {
    if (watching) return;
    watching = true;
    try {
      while (!Connected && !quitting) {
        try { await Connect(); break; } catch (Exception) { }
        if (State == "starting" && NowMs() - startedAt < 30000) { await Task.Delay(1000); continue; }
        if (system.LockfileBusy()) {
          var client = await CurrentClient();
          var now = NowMs();
          if (await TakeoverStep(client, now)) continue;
        } else if (State != "client-path-missing" || pendingChoice != "launch") SetState("client-off");
        await Task.Delay(1500);
      }
    } finally {
      watching = false;
    }
  }

  // ---- updates ------------------------------------------------------------------------------------------------------
  // Releases on GitHub carry the built folder as Lanes-<version>.zip (node build.cjs --release). Only files under this
  // repository's releases are taken. LANES_RELEASES_URL and LANES_ASSET_PREFIX point a test build at a local server.
  public sealed class UpdateInfo { public string State = "idle", Latest, Reason, Url; public long Size; public int Progress; }
  UpdateInfo update = new UpdateInfo();
  public string UpdateState { get { return update.State; } }
  static readonly HttpClient web = CreateWebClient();

  static HttpClient CreateWebClient() {
    // A program built without a target framework attribute starts with SSL 3 and TLS 1.0 only; GitHub needs TLS 1.2.
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
  }

  string ReleasesUrl { get { return Environment.GetEnvironmentVariable("LANES_RELEASES_URL") ?? "https://api.github.com/repos/" + Repository + "/releases/latest"; } }
  string AssetPrefix { get { return Environment.GetEnvironmentVariable("LANES_ASSET_PREFIX") ?? "https://github.com/" + Repository + "/releases/download/"; } }

  public static bool IsNewer(string candidate, string current) {
    var a = candidate.Split('.').Select(p => { int n; return int.TryParse(p, out n) ? n : 0; }).ToArray();
    var b = current.Split('.').Select(p => { int n; return int.TryParse(p, out n) ? n : 0; }).ToArray();
    for (var i = 0; i < 3; i++) {
      var x = i < a.Length ? a[i] : 0;
      var y = i < b.Length ? b[i] : 0;
      if (x != y) return x > y;
    }
    return false;
  }

  // The release's package: Lanes-<version>.zip under this repository's release downloads, or null. GitHub names are
  // case-insensitive, and its links use the repository's current spelling.
  public static Dictionary<string, object> ReleaseAsset(Dictionary<string, object> release, string latest, string prefix) {
    foreach (var item in Json.Arr(Json.Get(release, "assets")) ?? new object[0]) {
      var asset = Json.Obj(item);
      if (Json.Str(Json.Get(asset, "name")) == "Lanes-" + latest + ".zip" && Json.Str(Json.Get(asset, "browser_download_url")).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return asset;
    }
    return null;
  }

  // Lanes mostly runs hidden, so it also checks by itself (Start). An automatic check that fails only logs and keeps
  // the previous result, so an unreachable GitHub does not leave an error in settings; it never replaces a found release.
  public async Task CheckUpdate(bool manual) {
    if (update.State == "checking" || update.State == "downloading" || update.State == "ready" || (!manual && update.State == "available")) return;
    var before = update;
    update = new UpdateInfo { State = "checking" };
    try {
      using (var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl))
      using (var timeout = new CancellationTokenSource(15000)) {
        request.Headers.UserAgent.ParseAdd("Lanes/" + Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using (var response = await web.SendAsync(request, timeout.Token)) {
          var status = (int)response.StatusCode;
          if (status == 404) update = new UpdateInfo { State = "latest" }; // no release published yet
          else if (!response.IsSuccessStatusCode) update = new UpdateInfo { State = "failed", Reason = status == 403 || status == 429 ? "GitHub rate limit" : "HTTP " + status };
          else {
            var release = Json.Obj(Json.Read(await response.Content.ReadAsStringAsync()));
            var tag = Json.Str(Json.Get(release, "tag_name"));
            var latest = Regex.Replace(tag, "^v", "", RegexOptions.IgnoreCase);
            var asset = ReleaseAsset(release, latest, AssetPrefix);
            if (!Regex.IsMatch(latest, @"^\d+\.\d+\.\d+$")) update = new UpdateInfo { State = "failed", Reason = "unexpected tag " + tag };
            else if (!IsNewer(latest, Version)) update = new UpdateInfo { State = "latest", Latest = latest };
            else if (asset == null) update = new UpdateInfo { State = "failed", Reason = "release " + latest + " has no Lanes-" + latest + ".zip" };
            else update = new UpdateInfo { State = "available", Latest = latest, Url = Json.Str(Json.Get(asset, "browser_download_url")), Size = (long)Json.Num(Json.Get(asset, "size")) };
          }
        }
      }
    } catch (Exception e) {
      update = new UpdateInfo { State = "failed", Reason = e is OperationCanceledException ? "timed out" : Inner(e).Message };
    }
    log((manual ? "Update check" : "Automatic update check") + ": " + update.State + (update.Latest != null ? " (" + update.Latest + ")" : "") + (update.Reason != null ? " - " + update.Reason : ""));
    if (!manual && update.State == "failed") update = before;
  }

  static Exception Inner(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

  // Waits for Lanes to exit, copies the new files over the folder except the user's settings and log, and starts Lanes
  // again. A copy that fails part way (a file still locked) would leave old and new files mixed, so the folder is backed
  // up first and restored on failure; without a complete backup nothing is copied. update-result.txt tells the next
  // start what happened.
  public static readonly string ApplyScript = string.Join("\r\n", new[] {
    "param([string]$Source, [string]$Target, [string]$Wait, [string]$Work)",
    "foreach ($id in ($Wait -split ',')) { Wait-Process -Id ([int]$id) -Timeout 60 -ErrorAction SilentlyContinue }",
    "Start-Sleep -Milliseconds 500",
    "$result = Join-Path $Target 'update-result.txt'",
    "$backup = Join-Path (Split-Path $Source -Parent) 'backup'",
    "robocopy $Target $backup /E /XD dist /XF settings.json lanes.log /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
    "$code = $LASTEXITCODE",
    "if ($code -ge 8) { Set-Content -Path $result -Value \"Update not applied: the current version could not be backed up (robocopy exit $code)\" }",
    "else {",
    "  robocopy $Source $Target /E /XF settings.json lanes.log /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
    "  $code = $LASTEXITCODE",
    "  if ($code -lt 8) { Set-Content -Path $result -Value 'Update applied' }",
    "  else {",
    "    robocopy $backup $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
    "    $restore = $LASTEXITCODE",
    "    if ($restore -lt 8) {",
    // The copy back leaves what the update added on top of the old version: those files and folders go, and only
    // those (the package's own paths that the backup does not have).
    "      $src = (Get-Item -LiteralPath $Source).FullName",
    "      Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {",
    "        $rel = $_.FullName.Substring($src.Length).TrimStart([char]92)",
    "        if ($rel -ne 'settings.json' -and $rel -ne 'lanes.log' -and -not (Test-Path -LiteralPath (Join-Path $backup $rel))) { Remove-Item -LiteralPath (Join-Path $Target $rel) -Force -ErrorAction SilentlyContinue }",
    "      }",
    "      Get-ChildItem -LiteralPath $src -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {",
    "        $rel = $_.FullName.Substring($src.Length).TrimStart([char]92)",
    "        $dir = Join-Path $Target $rel",
    "        if (-not (Test-Path -LiteralPath (Join-Path $backup $rel)) -and (Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force)) { Remove-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue }",
    "      }",
    "      Set-Content -Path $result -Value \"Update failed (robocopy exit $code); the previous version was restored\"",
    "    }",
    "    else { Set-Content -Path $result -Value \"Update failed (robocopy exit $code) and restoring the previous version failed too (robocopy exit $restore); the previous version is kept in $backup\"; $keep = $true }",
    "  }",
    "}",
    // The download, the unpacked files and the backup are not needed any more, unless the backup is the only complete
    // copy left.
    "if (-not $keep -and (Split-Path $Work -Leaf) -like 'lanes-update-*') { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }",
    "Start-Process -FilePath (Join-Path $Target 'Lanes.exe')"
  });

  // Download and unpack the release, then leave a script that swaps the files in once Lanes has exited (Lanes.exe is
  // locked while it runs), keeps settings.json and lanes.log, and starts Lanes again. The window quits on "ready".
  public async Task<bool> InstallUpdate() {
    if (update.State != "available") return false;
    var target = update;
    update = new UpdateInfo { State = "downloading", Latest = target.Latest, Url = target.Url, Size = target.Size };
    string work = null;
    try {
      work = Path.Combine(Path.GetTempPath(), "lanes-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
      Directory.CreateDirectory(work);
      var zip = Path.Combine(work, "package.zip");
      var files = Path.Combine(work, "files");
      log("Downloading the update (" + (target.Size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB)");
      await Download(target.Url, zip, target.Size, progress => update.Progress = progress, 60000);
      await Task.Run(() => ZipFile.ExtractToDirectory(zip, files));
      var root = File.Exists(Path.Combine(files, "Lanes.exe")) ? files : Path.Combine(files, "Lanes");
      foreach (var file in new[] { "Lanes.exe", "version.json" }) if (!File.Exists(Path.Combine(root, file))) throw new InvalidDataException("the package has no " + file);
      var script = Path.Combine(work, "apply.ps1");
      File.WriteAllText(script, ApplyScript, new UTF8Encoding(false));
      // A process started by .NET keeps running after Lanes exits. Paths are double-quoted (a Windows path cannot
      // contain one) and have no trailing backslash, which would escape the closing quote.
      var arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -Source \"" + root + "\" -Target \"" + ArgumentPath(folder) + "\" -Wait " + Process.GetCurrentProcess().Id + " -Work \"" + work + "\"";
      Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
      update = new UpdateInfo { State = "ready", Latest = target.Latest, Progress = 100 };
      log("Update " + target.Latest + " downloaded; it is applied after Lanes exits");
      return true;
    } catch (Exception e) {
      update = new UpdateInfo { State = "install-failed", Latest = target.Latest, Reason = Inner(e).Message };
      log("Update failed: " + Inner(e).Message);
      try { if (work != null) Directory.Delete(work, true); } catch (Exception) { }
      return false;
    }
  }

  // A folder for the update script's command line: no trailing backslash, and a drive root as "D:\." ("D:" alone would
  // mean the current directory on D:).
  public static string ArgumentPath(string path) {
    var trimmed = path.TrimEnd('\\');
    return trimmed.EndsWith(":") ? trimmed + @"\." : trimmed;
  }

  // Streams a file to disk, reporting whole percents. A slow download goes on as long as data keeps coming; it stops
  // after stallMs without any. A byte count other than the expected size (GitHub's asset size, else Content-Length)
  // fails, so a cut transfer is never unpacked.
  public static async Task Download(string url, string file, long size, Action<int> onProgress, int stallMs) {
    using (var stall = new CancellationTokenSource()) {
      stall.CancelAfter(stallMs);
      try {
        using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
          request.Headers.UserAgent.ParseAdd("Lanes");
          using (var response = await web.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)) {
            if (!response.IsSuccessStatusCode) throw new IOException("download failed: HTTP " + (int)response.StatusCode);
            var expected = size > 0 ? size : response.Content.Headers.ContentLength ?? 0;
            using (var body = await response.Content.ReadAsStreamAsync())
            using (stall.Token.Register(() => body.Dispose()))
            using (var output = new FileStream(file, FileMode.Create, FileAccess.Write)) {
              var buffer = new byte[64 * 1024];
              long received = 0;
              while (true) {
                stall.CancelAfter(stallMs);
                var count = await body.ReadAsync(buffer, 0, buffer.Length);
                if (count == 0) break;
                output.Write(buffer, 0, count);
                received += count;
                if (expected > 0) onProgress((int)Math.Min(100, received * 100 / expected));
              }
              if (expected > 0 && received != expected) throw new IOException("download incomplete: " + received + " of " + expected + " bytes");
            }
          }
        }
      } catch (Exception) {
        if (stall.IsCancellationRequested) throw new TimeoutException("no data for " + (stallMs / 1000.0).ToString(CultureInfo.InvariantCulture) + " s");
        throw;
      }
    }
  }

  // ---- start and stop ------------------------------------------------------------------------------------------------
  DispatcherTimer pollTimer, updateTimer;

  public void Start(bool login) {
    log("Controller " + Version + (login ? " (started at sign-in)" : "") + ", Bilibili executable: " + (Json.Str(Settings["clientExe"]).Length > 0 ? Settings["clientExe"] : "not known yet"));
    var result = Path.Combine(folder, "update-result.txt");
    try { if (File.Exists(result)) { log(File.ReadAllText(result).Trim()); File.Delete(result); } } catch (Exception) { }
    pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMs) };
    pollTimer.Tick += delegate { if (Connected) Ignore(Poll()); };
    pollTimer.Start();
    // Installing restarts Lanes, so it waits for the Update button; this only finds the release.
    updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
    updateTimer.Tick += delegate { updateTimer.Interval = TimeSpan.FromHours(24); Ignore(CheckUpdate(false)); };
    updateTimer.Start();
    Begin();
  }

  async void Begin() {
    // Lanes never opens the client by itself: it waits, and connects to whatever client appears. One that is already
    // running now is restarted only with "connect to a Bilibili that is already open" on. It is noted before the first
    // connection attempt, which takes about 2 s to fail: a client opened meanwhile is a new one.
    if (system.LockfileBusy()) { var client = await CurrentClient(); initialClient = client != null ? client.Key : null; }
    try { await Connect(); return; } catch (Exception) { }
    WatchClient();
  }

  // Stop accelerating new requests; downloads already running inside the page finish on their own. The page lease
  // covers a Lanes that is killed instead.
  public async Task Shutdown() {
    quitting = true;
    log("Controller exiting");
    var connection = cdp;
    if (connection == null) return;
    var off = pages.Keys.ToList().Select(id => connection.Evaluate(id, "globalThis.__BTR_LOCAL__ && globalThis.__BTR_DESKTOP__?.setSettings({ enabled: false }), 0", 3000).ContinueWith(t => { var unused = t.Exception; }));
    await Task.WhenAll(off);
    connection.Abort();
  }
}
