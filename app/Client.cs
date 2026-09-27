// The Bilibili client as a set of Windows processes: is it running, which run is it, where is its executable, and
// how to stop exactly one run and start it again with the debugging port. Everything that touches the system goes
// through IClientSystem, so the self-check can drive the rules with a fake process table.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

public sealed class ProcessInfo {
  public int Id, ParentId;
  public string Name = "";
  public long StartedMs; // start time, Unix milliseconds; 0 when it could not be read
}

// One run of the client: its earliest process (the browser process that owns the others).
public sealed class ClientRun {
  public int Id;
  public long StartedMs;
  public string Path = ""; // "" when it could not be read
  public string Key { get { return Id + "@" + StartedMs; } }
}

public interface IClientSystem {
  bool LockfileBusy();                 // the running client holds its lockfile open
  long LockfileStamp();                // changes whenever the client starts again
  List<ProcessInfo> LockfileUsers();   // the processes holding the lockfile (id + start time); null when unknown
  List<ProcessInfo> Snapshot();        // every process with its parent and start time
  string ExecutablePath(int id);       // "" when it cannot be read
  bool FileExists(string path);
  bool Kill(int id, long startedMs);   // only if that process id still has that start time
  void Launch(string path, string arguments);
  string RegistryValue(string root, string key, string name); // null when absent
}

public static class Client {
  // The client's executable name (four CJK characters), built from code points so the source stays ASCII.
  public static readonly string Name = new string(new[] { (char)0x54D4, (char)0x54E9, (char)0x54D4, (char)0x54E9 });
  public static readonly string ExeName = Name + ".exe";
  public static readonly string DefaultExe = Path.Combine(@"C:\Program Files\bilibili", ExeName);

  // A path to the client's executable: fully qualified ("C:\..." or "\\server\..."; "C:foo" and "\foo" would depend on
  // the current directory) and named like the client's executable.
  public static bool IsClientPath(string path) {
    if (string.IsNullOrEmpty(path)) return false;
    try {
      var root = Path.GetPathRoot(path) ?? "";
      var absolute = (root.Length >= 3 && root[1] == ':') || root.StartsWith(@"\\") || root.StartsWith("//");
      return absolute && string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase);
    } catch (ArgumentException) { return false; }
  }

  // A path Lanes may launch: a client path that is present.
  public static bool Launchable(IClientSystem system, string path) { return IsClientPath(path) && system.FileExists(path); }

  // The client-named process that holds the lockfile is the browser process of the running client: one run (id + start
  // time) and its path. Not the earliest client-named process: a child of a run that ended abnormally can outlive it and
  // is older than the next run. null when no single such process is found (the caller tries again later).
  public static ClientRun Identify(IClientSystem system) {
    var users = system.LockfileUsers();
    if (users == null) return null;
    var browsers = system.Snapshot().Where(p => string.Equals(p.Name, ExeName, StringComparison.OrdinalIgnoreCase) && p.StartedMs != 0 && users.Any(u => u.Id == p.Id && u.StartedMs == p.StartedMs)).ToList();
    if (browsers.Count != 1) return null;
    var browser = browsers[0];
    var path = system.ExecutablePath(browser.Id);
    return new ClientRun { Id = browser.Id, StartedMs = browser.StartedMs, Path = Launchable(system, path) ? Path.GetFullPath(path) : "" };
  }

  // The processes of that run in a snapshot: the browser process and everything below it. Nothing when the browser
  // process is gone or its id now belongs to another start time, so a run that appeared meanwhile is never touched.
  public static List<ProcessInfo> RunProcesses(ClientRun run, List<ProcessInfo> snapshot) {
    var result = new List<ProcessInfo>();
    var main = snapshot.FirstOrDefault(p => p.Id == run.Id);
    if (main == null || main.StartedMs != run.StartedMs) return result;
    result.Add(main);
    for (var i = 0; i < result.Count; i++) {
      var parent = result[i];
      // A child starts after its parent; an older process with a reused parent id is not part of this run.
      foreach (var child in snapshot) if (child.ParentId == parent.Id && child.StartedMs >= parent.StartedMs && child.Id != parent.Id && !result.Contains(child)) result.Add(child);
    }
    return result;
  }

  // Stops exactly one run. False when that run is no longer there as it was identified, or ended by itself before it
  // could be stopped (the user closed it): starting it again would reopen a client the user just closed. A browser
  // process that could not be ended but is still there (the client runs elevated) counts as stopped, so the caller's
  // wait for the lockfile reports that the client could not be closed.
  public static bool Stop(IClientSystem system, ClientRun run) {
    var processes = RunProcesses(run, system.Snapshot());
    if (processes.Count == 0) return false;
    var ended = system.Kill(processes[0].Id, processes[0].StartedMs);
    foreach (var process in processes.Skip(1)) system.Kill(process.Id, process.StartedMs);
    return ended || RunProcesses(run, system.Snapshot()).Count > 0;
  }

  // The install from the uninstall record: HKCU then HKLM, the normal and the WOW6432Node key, InstallLocation and then
  // the folder of DisplayIcon. null when none of them holds the executable.
  public static string FromUninstallRecord(IClientSystem system) {
    string[] keys = { @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\BiliBili" };
    foreach (var root in new[] { "HKCU", "HKLM" }) {
      foreach (var key in keys) {
        var location = system.RegistryValue(root, key, "InstallLocation");
        var icon = IconPath(system.RegistryValue(root, key, "DisplayIcon"));
        foreach (var folder in new[] { location, icon.Length == 0 ? null : SafeDirectory(icon) }) {
          if (string.IsNullOrEmpty(folder)) continue;
          var exe = SafeCombine(folder.Trim('"'), ExeName);
          if (exe != null && Launchable(system, exe)) return exe;
        }
      }
    }
    return null;
  }

  // DisplayIcon: a path, maybe quoted, maybe followed by ",<icon index>"; the path itself may contain commas.
  static string IconPath(string value) {
    var text = (value ?? "").Trim();
    if (text.StartsWith("\"")) { var end = text.IndexOf('"', 1); return end > 0 ? text.Substring(1, end - 1) : text.Trim('"'); }
    return Regex.Replace(text, @",\s*-?\d+$", "");
  }

  static string SafeDirectory(string path) { try { return Path.GetDirectoryName(path); } catch (ArgumentException) { return null; } }
  static string SafeCombine(string folder, string file) { try { return Path.Combine(folder, file); } catch (ArgumentException) { return null; } }
}

// The real system.
public sealed class WindowsClientSystem : IClientSystem {
  readonly string lockfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bilibili", "lockfile");

  // No process query: the lockfile is busy exactly while a client runs.
  public bool LockfileBusy() {
    try {
      using (new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
      return false;
    } catch (IOException e) {
      return (e.HResult & 0xFFFF) == 32; // ERROR_SHARING_VIOLATION
    } catch (Exception) {
      return false;
    }
  }

  public long LockfileStamp() {
    try { return File.GetLastWriteTimeUtc(lockfile).Ticks; } catch (Exception) { return 0; }
  }

  public List<ProcessInfo> LockfileUsers() { return FileUsers(lockfile); }

  // The processes that hold a file open, from Windows' Restart Manager (id + start time); null when it cannot tell.
  public static List<ProcessInfo> FileUsers(string path) {
    uint session;
    if (RmStartSession(out session, 0, new StringBuilder(64)) != 0) return null;
    try {
      if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return null;
      for (var attempt = 0; attempt < 3; attempt++) {
        uint needed, count = 0, reasons = 0;
        var result = RmGetList(session, out needed, ref count, null, ref reasons);
        if (result == 0) return new List<ProcessInfo>();
        if (result != 234 /* ERROR_MORE_DATA */) return null;
        var found = new RM_PROCESS_INFO[needed];
        count = needed;
        result = RmGetList(session, out needed, ref count, found, ref reasons);
        if (result == 0) return found.Take((int)count).Select(p => new ProcessInfo { Id = p.ProcessId, StartedMs = UnixMs(((long)p.StartHigh << 32) | p.StartLow) }).ToList();
        if (result != 234) return null; // 234: more users appeared meanwhile; ask again
      }
      return null;
    } finally { RmEndSession(session); }
  }

  public List<ProcessInfo> Snapshot() {
    var list = new List<ProcessInfo>();
    var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
    if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return list;
    try {
      var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
      for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry)) {
        // Start times only for the client's own processes: opening every process on the system is slow.
        var client = string.Equals(entry.szExeFile, Client.ExeName, StringComparison.OrdinalIgnoreCase);
        list.Add(new ProcessInfo { Id = (int)entry.th32ProcessID, ParentId = (int)entry.th32ParentProcessID, Name = entry.szExeFile, StartedMs = client ? StartedMs((int)entry.th32ProcessID) : 0 });
      }
    } finally { CloseHandle(snapshot); }
    return list;
  }

  static long StartedMs(int id) {
    var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, id);
    if (handle == IntPtr.Zero) return 0;
    try { return StartedMs(handle); } finally { CloseHandle(handle); }
  }

  static long StartedMs(IntPtr handle) {
    long created, exited, kernel, user;
    return GetProcessTimes(handle, out created, out exited, out kernel, out user) ? UnixMs(created) : 0;
  }

  static long UnixMs(long fileTime) { return (DateTime.FromFileTimeUtc(fileTime) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerMillisecond; }

  public string ExecutablePath(int id) {
    var handle = OpenProcess(0x1000, false, id);
    if (handle == IntPtr.Zero) return "";
    try {
      var text = new StringBuilder(1024);
      var size = text.Capacity;
      return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString() : "";
    } finally { CloseHandle(handle); }
  }

  public bool FileExists(string path) { return File.Exists(path); }

  // One handle for the check and the kill: while it is open, the id cannot pass to another process.
  public bool Kill(int id, long startedMs) {
    var handle = OpenProcess(0x1001 /* PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION */, false, id);
    if (handle == IntPtr.Zero) return false;
    try { return StartedMs(handle) == startedMs && TerminateProcess(handle, 1); } finally { CloseHandle(handle); }
  }

  public void Launch(string path, string arguments) {
    Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) });
  }

  public string RegistryValue(string root, string key, string name) {
    using (var hive = (root == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(key)) return hive == null ? null : hive.GetValue(name) as string;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct PROCESSENTRY32 {
    public uint dwSize, cntUsage, th32ProcessID;
    public IntPtr th32DefaultHeapID;
    public uint th32ModuleID, cntThreads, th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
  }
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
  [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
  [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exitCode);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

  // RM_PROCESS_INFO with its RM_UNIQUE_PROCESS inlined; the FILETIME is two 32-bit halves (4-byte aligned).
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct RM_PROCESS_INFO {
    public int ProcessId;
    public uint StartLow, StartHigh;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
    public int ApplicationType;
    public uint AppStatus, TSSessionId;
    public int Restartable;
  }
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
  [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, IntPtr[] processes, uint services, string[] serviceNames);
  [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] processes, ref uint rebootReasons);
}
