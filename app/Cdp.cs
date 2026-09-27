// Chrome DevTools Protocol client for the Bilibili client's debugging port. Runs on the WPF dispatcher thread like
// the rest of the controller: every continuation comes back to that one thread, so no locks are needed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// What the CDP client needs from a connection; the self-check swaps in a fake one.
public interface ICdpTransport {
  Task SendAsync(string text);
  Task<string> ReceiveAsync(); // null when the connection is closed
  void Abort();
}

public sealed class SocketTransport : ICdpTransport {
  readonly ClientWebSocket socket = new ClientWebSocket();
  readonly byte[] buffer = new byte[64 * 1024];

  // A client that accepts the connection and never answers the handshake would hold it forever; the caller retries.
  public static async Task<SocketTransport> Connect(string url, int timeoutMs) {
    var transport = new SocketTransport();
    using (var timeout = new CancellationTokenSource(timeoutMs)) {
      try { await transport.socket.ConnectAsync(new Uri(url), timeout.Token); }
      catch (Exception) { transport.socket.Dispose(); throw; }
    }
    return transport;
  }

  public Task SendAsync(string text) {
    return socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);
  }

  // A message can arrive in several frames: read until EndOfMessage.
  public async Task<string> ReceiveAsync() {
    var message = new MemoryStream();
    while (true) {
      var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
      if (result.MessageType == WebSocketMessageType.Close) return null;
      message.Write(buffer, 0, result.Count);
      if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
    }
  }

  // The DevTools server does not finish the close handshake, so CloseAsync would throw: just drop the connection.
  public void Abort() {
    try { socket.Abort(); } catch (Exception) { }
    socket.Dispose();
  }
}

public sealed class Cdp {
  readonly ICdpTransport transport;
  readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> waits = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
  // ClientWebSocket allows one send at a time; one queue keeps commands in the order they were issued.
  readonly Queue<string> outbox = new Queue<string>();
  int nextId;
  bool pumping, closed;

  public Action<string, Dictionary<string, object>> OnEvent; // method, message (params and sessionId inside)
  public Action OnClosed;

  public Cdp(ICdpTransport transport) { this.transport = transport; }

  public bool Closed { get { return closed; } }

  public void Start() { ReceiveLoop(); }

  // Queues the command at once (callers rely on the order of their calls, not of their awaits). timeoutMs 0 waits
  // for as long as the connection lives: a paused page answers nothing until it is released.
  public Task<Dictionary<string, object>> Send(string method, Dictionary<string, object> parameters, string sessionId, int timeoutMs) {
    var done = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (closed) { done.SetException(new IOException("CDP closed")); return done.Task; }
    var id = ++nextId;
    waits[id] = done;
    var message = new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", parameters ?? new Dictionary<string, object>() } };
    if (sessionId != null) message["sessionId"] = sessionId;
    outbox.Enqueue(Json.Write(message));
    Pump();
    if (timeoutMs > 0) Expire(id, method, timeoutMs);
    return done.Task;
  }

  // A page that is busy or paused does not answer; give up on it without leaving the wait behind.
  async void Expire(int id, string method, int timeoutMs) {
    await Task.Delay(timeoutMs);
    TaskCompletionSource<Dictionary<string, object>> done;
    if (waits.TryGetValue(id, out done)) { waits.Remove(id); done.TrySetException(new TimeoutException(method + " timeout")); }
  }

  async void Pump() {
    if (pumping) return;
    pumping = true;
    try {
      while (outbox.Count > 0 && !closed) await transport.SendAsync(outbox.Dequeue());
    } catch (Exception) {
      Close();
    } finally {
      pumping = false;
    }
  }

  async void ReceiveLoop() {
    try {
      while (true) {
        var text = await transport.ReceiveAsync();
        if (text == null) break;
        Handle(text);
      }
    } catch (Exception) { }
    Close();
  }

  void Handle(string text) {
    Dictionary<string, object> message;
    try { message = Json.Read(text) as Dictionary<string, object>; } catch (Exception) { return; }
    if (message == null) return;
    object idValue;
    if (message.TryGetValue("id", out idValue) && idValue != null) {
      var id = Json.Int(idValue);
      TaskCompletionSource<Dictionary<string, object>> done;
      if (!waits.TryGetValue(id, out done)) return;
      waits.Remove(id);
      var error = Json.Obj(Json.Get(message, "error"));
      if (error != null) done.TrySetException(new InvalidOperationException(Json.Str(Json.Get(error, "message"))));
      else done.TrySetResult(Json.Obj(Json.Get(message, "result")) ?? new Dictionary<string, object>());
      return;
    }
    var method = Json.Str(Json.Get(message, "method"));
    if (method.Length > 0 && OnEvent != null) OnEvent(method, message);
  }

  void Close() {
    if (closed) return;
    closed = true;
    foreach (var done in waits.Values) done.TrySetException(new IOException("CDP closed"));
    waits.Clear();
    outbox.Clear();
    if (OnClosed != null) OnClosed();
  }

  public void Abort() {
    transport.Abort();
    Close();
  }

  // Runtime.evaluate returning the value, or null when the page threw or answered nothing usable.
  public async Task<object> Evaluate(string sessionId, string expression, int timeoutMs) {
    var result = await Send("Runtime.evaluate", new Dictionary<string, object> { { "expression", expression }, { "returnByValue", true } }, sessionId, timeoutMs);
    if (result.ContainsKey("exceptionDetails")) return null;
    return Json.Get(Json.Obj(Json.Get(result, "result")), "value");
  }
}
