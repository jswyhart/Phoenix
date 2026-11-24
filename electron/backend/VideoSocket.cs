using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Phoenix.Backend;

/// <summary>
/// Hands the playing video's frames to the window over a WebSocket on this machine only (127.0.0.1, a port the system
/// picks, and a token made at each launch that the page gets from the main process). One frame travels at a time: the
/// next is sent only after the page says it drew the last, so a slow page drops frames instead of falling behind.
/// Each message is a 16-byte header (width, height, luma stride, chroma stride) followed by the Y, U and V planes.
/// </summary>
public sealed class VideoSocket
{
    readonly VideoOut _video;
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    readonly AutoResetEvent _frame = new(false);
    readonly SemaphoreSlim _drawn = new(0);
    readonly object _clientLock = new();
    WebSocket? _client;
    byte[] _buffer = Array.Empty<byte>();

    public int Port { get; }
    public string Token => _token;
    /// <summary>Frames sent to the page, and the time spent copying and sending them (for measurement).</summary>
    public int Sent { get; private set; }
    public double SendMs { get; private set; }

    public VideoSocket(VideoOut video)
    {
        _video = video;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        video.FrameReady += () => _frame.Set();
        new Thread(Accept) { IsBackground = true, Name = "video-accept" }.Start();
        new Thread(Send) { IsBackground = true, Name = "video-send" }.Start();
    }

    public void ResetStats() { Sent = 0; SendMs = 0; }

    void Accept()
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = _listener.AcceptTcpClient(); } catch { return; }
            _ = Task.Run(() => Serve(tcp));
        }
    }

    async Task Serve(TcpClient tcp)
    {
        WebSocket? ws = null;
        try
        {
            tcp.NoDelay = true;
            tcp.SendBufferSize = 4 << 20;
            var stream = tcp.GetStream();
            // read the upgrade request (a few hundred bytes)
            var head = new byte[8192];
            var n = 0;
            stream.ReadTimeout = 5000;
            while (n < head.Length && !Encoding.ASCII.GetString(head, 0, n).Contains("\r\n\r\n"))
            {
                var got = await stream.ReadAsync(head.AsMemory(n));
                if (got <= 0) return;
                n += got;
            }
            var lines = Encoding.ASCII.GetString(head, 0, n).Split("\r\n");
            var target = lines[0].Split(' ') is { Length: >= 2 } parts && parts[0] == "GET" ? parts[1] : "";
            string Header(string name) => lines.Skip(1).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
            var given = target.StartsWith("/video?token=") ? target["/video?token=".Length..] : "";
            var key = Header("Sec-WebSocket-Key");
            // only the app's own page, holding this launch's token, may connect
            var allowed = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(_token))
                && Header("Origin") == "phoenix://app" && key != "";
            if (!allowed)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"));
                return;
            }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            lock (_clientLock) { try { _client?.Abort(); } catch { } _client = ws; }
            _frame.Set();   // show the frame that is already there (a paused video)
            // every message from the page means "drawn, send the next one"
            var scratch = new byte[64];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(scratch, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (_drawn.CurrentCount == 0) _drawn.Release();
            }
        }
        catch { /* the page went away */ }
        finally
        {
            lock (_clientLock) if (ReferenceEquals(_client, ws)) _client = null;
            try { ws?.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            if (_drawn.CurrentCount == 0) _drawn.Release();
        }
    }

    unsafe void Send()
    {
        var first = true;
        while (true)
        {
            _frame.WaitOne();
            WebSocket? ws;
            lock (_clientLock) ws = _client;
            if (ws == null || ws.State != WebSocketState.Open) { first = true; continue; }
            // wait for the page to have drawn the previous frame (but not for ever: it may have been hidden)
            if (!first) _drawn.Wait(1000);
            first = false;
            while (_drawn.CurrentCount > 0) _drawn.Wait(0);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var length = 0;
            var ok = _video.Read((buffer, _) =>
            {
                int w = _video.Width, h = _video.Height, ys = _video.LumaStride, cs = _video.ChromaStride;
                if (w <= 0 || h <= 0) return;
                int ySize = ys * h, cSize = cs * (h / 2);
                length = 16 + ySize + 2 * cSize;
                if (_buffer.Length < length) _buffer = new byte[length];
                BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(0), w);
                BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(4), h);
                BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(8), ys);
                BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(12), cs);
                // the planes sit one after another in VLC's buffer, each with spare rows at the end that are not sent
                var u = buffer + ys * _video.LumaLines;
                var v = u + cs * _video.ChromaLines;
                Marshal.Copy(buffer, _buffer, 16, ySize);
                Marshal.Copy(u, _buffer, 16 + ySize, cSize);
                Marshal.Copy(v, _buffer, 16 + ySize + cSize, cSize);
            });
            if (!ok || length == 0) { first = true; continue; }
            try
            {
                ws.SendAsync(new ArraySegment<byte>(_buffer, 0, length), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
                Sent++;
                SendMs += clock.Elapsed.TotalMilliseconds;
            }
            catch { first = true; }
        }
    }
}
