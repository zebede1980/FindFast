using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using static FastFind.Core.Api.PipeProtocol;

namespace FastFind.Core.Api;

/// <summary>The index hosted in another process, reached over a named pipe (see <see cref="PipeIndexServer"/>).</summary>
public sealed class PipeIndexClient : IIndexService
{
    private readonly NamedPipeClientStream _pipe;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BinaryReader>> _pending = new();
    private readonly Thread _reader;
    private volatile IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();
    private int _nextId;
    private volatile bool _closed;

    public bool IsRemote => true;
    public bool IsElevated { get; private set; }
    public int ServerProcessId { get; private set; }
    public bool IsConnected => !_closed;
    public IReadOnlyList<VolumeInfo> Volumes => _volumes;

    public event Action? VolumesChanged;
    public event Action? IndexChanged;
    /// <summary>Raised once, on a background thread, when the server goes away.</summary>
    public event Action? Disconnected;

    private PipeIndexClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "FastFind pipe reader" };
    }

    /// <summary>Connects to the index service for <paramref name="pipeName"/>, or returns null if it is not running or does not answer in time.</summary>
    public static PipeIndexClient? TryConnect(string pipeName, int timeoutMs = 300)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeoutMs);
            var client = new PipeIndexClient(pipe);
            client._reader.Start();
            var hello = client.Call(Op.Hello, w => w.Write(PipeProtocol.Version), 3000);
            int serverVersion = hello.ReadInt32();
            if (serverVersion != PipeProtocol.Version) { client.Dispose(); return null; }
            client.IsElevated = hello.ReadBoolean();
            client.ServerProcessId = hello.ReadInt32();
            client._volumes = ReadVolumes(hello);
            return client;
        }
        catch (Exception)
        {
            try { pipe?.Dispose(); } catch (Exception) { }
            return null;
        }
    }

    private void ReadLoop()
    {
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
                switch (kind)
                {
                    case Kind.Response:
                        if (_pending.TryRemove(id, out var ok)) ok.TrySetResult(reader);
                        break;
                    case Kind.Error:
                        if (_pending.TryRemove(id, out var failed))
                        {
                            string message = reader.ReadString();
                            if (message == "cancelled") failed.TrySetCanceled();
                            else failed.TrySetException(new IOException(message));
                        }
                        break;
                    case Kind.Event when op == Op.VolumesChanged:
                        _volumes = ReadVolumes(reader);
                        Raise(VolumesChanged);
                        break;
                    case Kind.Event when op == Op.IndexChanged:
                        Raise(IndexChanged);
                        break;
                }
            }
        }
        catch (Exception) { /* pipe broken */ }
        Close(raise: true);
    }

    // Handlers run on the pool so one that calls back into the client cannot block the reader that would deliver its answer.
    private static void Raise(Action? handler)
    {
        if (handler != null) Task.Run(handler);
    }

    private Task<BinaryReader> Send(Op op, Action<BinaryWriter>? body, out int id, bool expectResponse = true)
    {
        id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<BinaryReader>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closed) { tcs.TrySetException(new IOException("The index service is not connected.")); return tcs.Task; }
        if (expectResponse) _pending[id] = tcs;
        try
        {
            WriteFrame(_pipe, _writeLock, Kind.Request, id, op, body);
            if (!expectResponse) tcs.TrySetResult(null!);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetException(new IOException("The index service is not connected.", ex));
            Close(raise: true);
        }
        return tcs.Task;
    }

    private BinaryReader Call(Op op, Action<BinaryWriter>? body, int timeoutMs)
    {
        var task = Send(op, body, out int id);
        if (!task.Wait(timeoutMs))
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException("The index service did not answer in time.");
        }
        return task.GetAwaiter().GetResult();
    }

    private void Post(Op op, Action<BinaryWriter>? body)
    {
        var task = Send(op, body, out _, expectResponse: false);
        // One-way: nobody waits for it, so a failure (the service has gone) must not resurface later as an unobserved exception.
        if (task.IsFaulted) _ = task.Exception;
    }

    public async Task<IResultSet> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var task = Send(Op.Search, w => Write(w, request), out int id);
        using var registration = ct.Register(() =>
        {
            Post(Op.Cancel, w => w.Write(id));
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetCanceled(ct);
        });
        var r = await task.ConfigureAwait(false);
        int resultId = r.ReadInt32();
        int count = r.ReadInt32();
        var elapsed = new TimeSpan(r.ReadInt64());
        string? error = ReadNullableString(r);
        return new RemoteResultSet(this, resultId, count, elapsed, error);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetFolderSizesAsync(string folder, CancellationToken ct = default)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = await Send(Op.FolderSizes, w => w.Write(folder), out _).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) sizes[r.ReadString()] = r.ReadInt64();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        return sizes;
    }

    public void Configure(IndexSettings settings) => Post(Op.Configure, w => Write(w, settings));
    public void Rebuild(string? root) => Post(Op.Rebuild, w => Write(w, root));
    public void RefreshVolumes() => Post(Op.RefreshVolumes, null);

    private void Close(bool raise)
    {
        if (_closed) return;
        _closed = true;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new IOException("The index service disconnected."));
        try { _pipe.Dispose(); } catch (Exception) { }
        if (raise) Raise(Disconnected);
    }

    public void Dispose() => Close(raise: false);

    /// <summary>A result held by the server; rows are fetched a page at a time.</summary>
    private sealed class RemoteResultSet : IResultSet
    {
        private readonly PipeIndexClient _client;
        private readonly int _id;
        private int _released;

        public RemoteResultSet(PipeIndexClient client, int id, int count, TimeSpan elapsed, string? error)
        {
            _client = client;
            _id = id;
            Count = count;
            Elapsed = elapsed;
            Error = error;
        }

        public int Count { get; }
        public TimeSpan Elapsed { get; }
        public string? Error { get; }

        public FileEntry[] GetRows(int offset, int count)
        {
            if (offset < 0) offset = 0;
            count = Math.Max(0, Math.Min(count, Count - offset));
            if (count == 0) return Array.Empty<FileEntry>();
            FileEntry[]? rows = null;
            if (_released == 0)
            {
                try
                {
                    var r = _client.Call(Op.GetRows, w => { w.Write(_id); w.Write(offset); w.Write(count); }, 5000);
                    rows = ReadRows(r);
                }
                catch (Exception) { /* fall through to placeholders: a view must never crash because the service went away */ }
            }
            if (rows != null && rows.Length == count) return rows;
            var padded = new FileEntry[count];
            for (int i = 0; i < count; i++) padded[i] = rows != null && i < rows.Length ? rows[i] : new FileEntry(null, "", 0, 0, 0, 0);
            return padded;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _client.Post(Op.Release, w => w.Write(_id));
        }
    }
}
