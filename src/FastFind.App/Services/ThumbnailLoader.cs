using System.Windows.Media;
using System.Windows.Threading;

namespace FastFind.App.Services;

/// <summary>
/// Thumbnails for the tile view. A few background threads fetch them, newest request first, so the tiles just scrolled
/// to come before the ones scrolled past; requests for tiles that have gone off screen are dropped. Recent thumbnails
/// are kept, so scrolling back is instant.
/// </summary>
public static class ThumbnailLoader
{
    private const int Workers = 3;
    private const int CacheSize = 400;
    /// <summary>Requests beyond this many waiting are dropped, oldest first: they are for tiles long since scrolled past.</summary>
    private const int MaxWaiting = 600;

    private sealed record Ask(string Path, string Key, int Size, Dispatcher Dispatcher, Action<ImageSource> Deliver, CancellationToken Cancel);

    private static readonly object Gate = new();
    private static readonly LinkedList<Ask> Waiting = new(); // newest first
    private static readonly Dictionary<string, (ImageSource Image, long Used)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static long _clock;
    private static int _started;

    /// <summary>
    /// Asks for a picture's thumbnail at <paramref name="size"/> pixels. <paramref name="deliver"/> runs on the calling
    /// thread's dispatcher: at once if the thumbnail is to hand, otherwise when it arrives, unless cancelled first.
    /// </summary>
    /// <param name="version">Changes when the file does (its modified time), so an edited picture is fetched again.</param>
    public static void Request(string path, long version, int size, Action<ImageSource> deliver, CancellationToken cancel)
    {
        string key = $"{path}|{version}|{size}";
        ImageSource? ready = null;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                Cache[key] = (hit.Image, ++_clock);
                ready = hit.Image;
            }
            else
            {
                Waiting.AddFirst(new Ask(path, key, size, Dispatcher.CurrentDispatcher, deliver, cancel));
                while (Waiting.Count > MaxWaiting) Waiting.RemoveLast();
                Monitor.Pulse(Gate);
            }
        }
        if (ready != null) { deliver(ready); return; }
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            for (int i = 0; i < Workers; i++)
            {
                // Thumbnail handlers are COM objects that expect an STA thread.
                var thread = new Thread(Work) { IsBackground = true, Name = "FastFind tile thumbnails", Priority = ThreadPriority.BelowNormal };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }
    }

    private static void Work()
    {
        while (true)
        {
            Ask next;
            ImageSource? cached = null;
            lock (Gate)
            {
                while (Waiting.Count == 0) Monitor.Wait(Gate);
                next = Waiting.First!.Value;
                Waiting.RemoveFirst();
                if (next.Cancel.IsCancellationRequested) continue;
                if (Cache.TryGetValue(next.Key, out var hit)) cached = hit.Image; // another tile asked for the same picture
            }

            var image = cached;
            if (image == null)
            {
                try
                {
                    // A cloud file's thumbnail may still be in the making; its icon stands in until it is ready.
                    image = ShellThumbnails.Get(next.Path, next.Size, icon => Hand(next, icon), next.Cancel);
                }
                catch (Exception e) { Logger.Warn($"Thumbnail failed for {next.Path}: {e.Message}"); }
                if (image == null) continue;
                lock (Gate)
                {
                    Cache[next.Key] = (image, ++_clock);
                    if (Cache.Count > CacheSize)
                        Cache.Remove(Cache.MinBy(entry => entry.Value.Used).Key);
                }
            }
            Hand(next, image);
        }
    }

    private static void Hand(Ask request, ImageSource image)
    {
        if (request.Cancel.IsCancellationRequested) return;
        request.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!request.Cancel.IsCancellationRequested) request.Deliver(image);
        });
    }
}
