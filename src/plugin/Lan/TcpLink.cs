using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// A framed message channel over a single TCP socket. Frames are length-prefixed:
    ///   [int32 little-endian length][payload bytes].
    /// Reads happen on a background thread; complete frames are queued and drained on the game
    /// thread via TryDequeue (called from LanPeer.Update). Writes are synchronous + locked.
    ///
    /// The payload is exactly the ChimpWorld message the game produced (packet id + fields + optional
    /// transaction id) — we are only the transport; we never parse it.
    /// </summary>
    internal sealed class TcpLink : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Thread _reader;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        public event Action<TcpLink> OnClosed;
        public Action<string> Log;   // optional diagnostic sink

        /// <summary>Remote SteamID once the handshake has identified the peer (0 until then).</summary>
        public ulong RemoteId;

        public bool IsClosed => _closed;
        public string RemoteEndpoint { get; }

        public TcpLink(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;          // latency over throughput for a game control channel
            _stream = client.GetStream();
            RemoteEndpoint = client.Client?.RemoteEndPoint?.ToString() ?? "?";
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "LanTcpReader" };
            _reader.Start();
        }

        private void ReadLoop()
        {
            var lenBuf = new byte[4];
            string reason = "eof";
            try
            {
                while (!_closed)
                {
                    if (!ReadExact(lenBuf, 4)) { reason = "len-read-eof"; break; }
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len < 0 || len > 16 * 1024 * 1024) { reason = $"bad-length({len})"; break; }
                    var payload = new byte[len];
                    if (len > 0 && !ReadExact(payload, len)) { reason = "payload-read-eof"; break; }
                    _inbound.Enqueue(payload);
                }
            }
            catch (Exception e) { reason = "exception:" + e.Message; }
            try { Log?.Invoke($"[tcplink] read loop ended ({RemoteEndpoint}) reason={reason}"); } catch { }
            Close();
        }

        private bool ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = _stream.Read(buf, off, count - off); }
                catch { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Send(byte[] payload)
        {
            if (_closed) return;
            lock (_writeLock)
            {
                try
                {
                    var len = BitConverter.GetBytes(payload.Length);
                    _stream.Write(len, 0, 4);
                    if (payload.Length > 0) _stream.Write(payload, 0, payload.Length);
                    _stream.Flush();
                }
                catch { Close(); }
            }
        }

        public bool TryDequeue(out byte[] payload)
        {
            // A requeued frame takes priority (used when a frame arrived before its prerequisite, e.g.
            // a CH_SERVER frame before HELLO was processed).
            if (_holdover != null)
            {
                payload = _holdover;
                _holdover = null;
                return true;
            }
            return _inbound.TryDequeue(out payload);
        }

        private byte[] _holdover;

        /// <summary>Push a single frame back to be returned first on the next TryDequeue.</summary>
        public void Requeue(byte[] payload) => _holdover = payload;

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { OnClosed?.Invoke(this); } catch { }
        }

        public void Dispose() => Close();
    }
}
