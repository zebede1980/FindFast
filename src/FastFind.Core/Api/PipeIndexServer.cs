using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using static FastFind.Core.Api.PipeProtocol;

namespace FastFind.Core.Api;

/// <summary>
/// Serves an <see cref="IIndexService"/> over a named pipe so a non-elevated UI (or any other tool) can query an
/// index that lives in an elevated background process. Only file names and metadata cross the pipe, never contents.
/// </summary>
public sealed class PipeIndexServer : IDisposable
{
    private readonly IIndexService _service;
    private readonly string _pipeName;
    private readonly PipeSecurity? _security;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Session, byte> _sessions = new();
    private bool _first = true;

    public event Action<string>? Log;
    /// <summary>Called with the settings a client sent, after they were applied (lets the host persist them).</summary>
    public event Action<IndexSettings>? Configured;

    public int ConnectedClients => _sessions.Count;

    public PipeIndexServer(IIndexService service, string pipeName, PipeSecurity? security = null)
    {
        _service = service;
        _pipeName = pipeName;
        _security = security;
    }

    /// <summary>Creates the first pipe instance (throws if another process already owns the name) and starts accepting clients.</summary>
    public void Start()
    {
        var first = CreatePipe();
        _ = Task.Run(() => AcceptLoop(first));
    }

    private NamedPipeServerStream CreatePipe()
    {
        var options = PipeOptions.Asynchronous;
        // Refuse to start if something else already created this pipe name: it would be answering our clients.
        if (_first) { options |= PipeOptions.FirstPipeInstance; _first = false; }
        return _security != null
            ? NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options, 1 << 16, 1 << 16, _security)
            : new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly, 1 << 16, 1 << 16);
    }

    private async Task AcceptLoop(NamedPipeServerStream pipe)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var session = new Session(this, pipe);
                _sessions[session] = 0;
                _ = Task.Factory.StartNew(session.Run, TaskCreationOptions.LongRunning);
                pipe = CreatePipe();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Pipe accept failed: {ex.Message}");
                try { pipe.Dispose(); } catch (Exception) { }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                try { pipe = CreatePipe(); } catch (Exception ex2) { Log?.Invoke($"Pipe create failed: {ex2.Message}"); break; }
            }
        }
        try { pipe.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _sessions.Keys) s.Close();
    }

    /// <summary>One connected client: its open result sets and in-flight searches.</summary>
    private sealed class Session
    {
        private const int MaxResults = 64;
        private readonly PipeIndexServer _server;
        private readonly NamedPipeServerStream _pipe;
        private readonly object _writeLock = new();
        private readonly Dictionary<int, IResultSet> _results = new();
        private readonly Queue<int> _resultOrder = new();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
        private int _nextResultId;
        private volatile bool _closed;

        public Session(PipeIndexServer server, NamedPipeServerStream pipe)
        {
            _server = server;
            _pipe = pipe;
        }

        public void Run()
        {
            _server._service.VolumesChanged += OnVolumesChanged;
            _server._service.IndexChanged += OnIndexChanged;
            try
            {
                while (!_closed)
                {
                    var frame = ReadFrame(_pipe);
                    if (frame == null) break;
                    var reader = new BinaryReader(new MemoryStream(frame), Encoding.UTF8);
                    var kind = (Kind)reader.ReadByte();
                    int id = reader.ReadInt32();
                    var op = (Op)reader.ReadByte();
                    if (kind != Kind.Request) continue;
                    // Cheap calls are answered inline so they stay ordered; searches run on the pool so they can overlap and be cancelled.
                    if (op is Op.Search or Op.FolderSizes) _ = Task.Run(() => Handle(id, op, reader));
                    else Handle(id, op, reader);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or EndOfStreamException)
            {
                // client went away or sent garbage: drop the session
            }
            catch (Exception ex)
            {
                _server.Log?.Invoke($"Pipe session failed: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }

        private void Handle(int id, Op op, BinaryReader r)
        {
            try
            {
                switch (op)
                {
                    case Op.Hello:
                    {
                        int clientVersion = r.ReadInt32();
                        Respond(id, op, w =>
                        {
                            w.Write(PipeProtocol.Version);
                            w.Write(_server._service.IsElevated);
                            w.Write(Environment.ProcessId);
                            Write(w, _server._service.Volumes);
                        });
                        break;
                    }
                    case Op.Search:
                    {
                        var request = ReadSearchRequest(r);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_server._cts.Token);
                        _running[id] = cts;
                        try
                        {
                            var result = _server._service.SearchAsync(request, cts.Token).GetAwaiter().GetResult();
                            int resultId = Keep(result);
                            Respond(id, op, w =>
                            {
                                w.Write(resultId);
                                w.Write(result.Count);
                                w.Write(result.Elapsed.Ticks);
                                Write(w, result.Error);
                            });
                        }
                        finally { _running.TryRemove(id, out _); }
                        break;
                    }
                    case Op.GetRows:
                    {
                        int resultId = r.ReadInt32();
                        int offset = r.ReadInt32();
                        int count = Math.Clamp(r.ReadInt32(), 0, 100_000);
                        IResultSet? result;
                        lock (_results) _results.TryGetValue(resultId, out result);
                        var rows = result?.GetRows(offset, count) ?? Array.Empty<FileEntry>();
                        Respond(id, op, w => Write(w, rows));
                        break;
                    }
                    case Op.Release:
                    {
                        int resultId = r.ReadInt32();
                        lock (_results) if (_results.Remove(resultId, out var result)) result.Dispose();
                        break; // no response: the client does not wait for it
                    }
                    case Op.Cancel:
                    {
                        int target = r.ReadInt32();
                        if (_running.TryGetValue(target, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
                        break;
                    }
                    case Op.Rebuild:
                        _server._service.Rebuild(ReadNullableString(r));
                        break;
                    case Op.RefreshVolumes:
                        _server._service.RefreshVolumes();
                        break;
                    case Op.Configure:
                    {
                        var settings = ReadSettings(r);
                        _server._service.Configure(settings);
                        _server.Configured?.Invoke(settings);
                        break;
                    }
                    case Op.FolderSizes:
                    {
                        string folder = r.ReadString();
                        var sizes = _server._service.GetFolderSizesAsync(folder, _server._cts.Token).GetAwaiter().GetResult();
                        Respond(id, op, w =>
                        {
                            w.Write(sizes.Count);
                            foreach (var kv in sizes) { w.Write(kv.Key); w.Write(kv.Value); }
                        });
                        break;
                    }
                    default:
                        Fail(id, op, $"Unknown operation {(int)op}");
                        break;
                }
            }
            catch (OperationCanceledException) { Fail(id, op, "cancelled"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Close(); }
            catch (Exception ex) { Fail(id, op, ex.Message); }
        }

        private int Keep(IResultSet result)
        {
            lock (_results)
            {
                int id = ++_nextResultId;
                _results[id] = result;
                _resultOrder.Enqueue(id);
                // A client that forgets to release results must not grow the server without bound.
                while (_results.Count > MaxResults && _resultOrder.TryDequeue(out int oldest))
                    if (_results.Remove(oldest, out var old)) old.Dispose();
                return id;
            }
        }

        private void Respond(int id, Op op, Action<BinaryWriter> body) => Send(Kind.Response, id, op, body);

        private void Fail(int id, Op op, string message)
        {
            try { Send(Kind.Error, id, op, w => w.Write(message)); } catch (Exception) { }
        }

        private void Send(Kind kind, int id, Op op, Action<BinaryWriter>? body)
        {
            if (_closed) return;
            WriteFrame(_pipe, _writeLock, kind, id, op, body);
        }

        private int _volumesPending, _indexPending;

        // Events are sent from the pool and coalesced, so a client that has stopped reading can never stall the indexer.
        private void OnVolumesChanged()
        {
            if (Interlocked.Exchange(ref _volumesPending, 1) != 0) return;
            Task.Run(() =>
            {
                Interlocked.Exchange(ref _volumesPending, 0);
                try { Send(Kind.Event, 0, Op.VolumesChanged, w => Write(w, _server._service.Volumes)); }
                catch (Exception) { Close(); }
            });
        }

        private void OnIndexChanged()
        {
            if (Interlocked.Exchange(ref _indexPending, 1) != 0) return;
            Task.Run(() =>
            {
                Interlocked.Exchange(ref _indexPending, 0);
                try { Send(Kind.Event, 0, Op.IndexChanged, null); }
                catch (Exception) { Close(); }
            });
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _server._service.VolumesChanged -= OnVolumesChanged;
            _server._service.IndexChanged -= OnIndexChanged;
            _server._sessions.TryRemove(this, out _);
            foreach (var cts in _running.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            lock (_results)
            {
                foreach (var r in _results.Values) r.Dispose();
                _results.Clear();
            }
            try { _pipe.Dispose(); } catch (Exception) { }
        }
    }
}
