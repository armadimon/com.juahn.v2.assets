using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Juahn.V2.Assets
{
    /// <summary>
    /// Thread-safe shared loads with per-consumer cancellation and counted leases.
    /// Unowned resources are released, not retained indefinitely. Use AssetScope for prewarming.
    /// Dispose ends the provider lifetime and invalidates every outstanding lease.
    /// </summary>
    public sealed class AssetCache<T> : IAssetProvider<T>, IDisposable where T : class
    {
        private sealed class Entry
        {
            public readonly TaskCompletionSource<AssetResource<T>> Completion =
                new TaskCompletionSource<AssetResource<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            public AssetResource<T> Resource;
            public int Waiters;
            public int Leases;
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly IAssetSource<T> _source;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationToken _token;
        private bool _disposed;
        public AssetCache(IAssetSource<T> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _token = _lifetime.Token;
        }
        public int EntryCount { get { lock (_gate) return _entries.Count; } }

        public async Task<AssetLease<T>> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An asset key is required.", nameof(key));
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry;
            bool start;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AssetCache<T>));
                start = !_entries.TryGetValue(key, out entry);
                if (start)
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                    // Observe failures even when all callers have canceled their waits.
                    _ = entry.Completion.Task.ContinueWith(t => { _ = t.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                entry.Waiters++;
            }
            if (start) _ = LoadAsync(key, entry);
            var ownsWaiter = true;
            try
            {
                var resource = await WaitAsync(entry.Completion.Task, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed) throw new ObjectDisposedException(nameof(AssetCache<T>));
                    entry.Waiters--;
                    entry.Leases++;
                    ownsWaiter = false;
                }
                return new AssetLease<T>(key, resource.Value, () => Release(key, entry, false));
            }
            finally { if (ownsWaiter) Release(key, entry, true); }
        }

        private async Task LoadAsync(string key, Entry entry)
        {
            try
            {
                var resource = await _source.LoadAsync(key, _token).ConfigureAwait(false);
                if (resource == null) throw new InvalidOperationException("The asset source returned no resource: " + key);
                bool release;
                lock (_gate)
                {
                    entry.Resource = resource;
                    release = _disposed || entry.Waiters + entry.Leases == 0;
                    if (release) Remove(key, entry);
                }
                // Complete before cleanup; a faulty backend release must not strand waiters.
                entry.Completion.TrySetResult(resource);
                if (release) resource.Dispose();
            }
            catch (Exception error)
            {
                lock (_gate) Remove(key, entry);
                if (error is OperationCanceledException) entry.Completion.TrySetCanceled();
                else entry.Completion.TrySetException(error);
            }
        }

        private void Release(string key, Entry entry, bool waiter)
        {
            AssetResource<T> resource = null;
            lock (_gate)
            {
                if (waiter) entry.Waiters--; else entry.Leases--;
                if (entry.Waiters + entry.Leases == 0 && entry.Resource != null)
                {
                    Remove(key, entry);
                    resource = entry.Resource;
                }
            }
            resource?.Dispose();
        }
        private void Remove(string key, Entry entry)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
        }
        private static async Task<TValue> WaitAsync<TValue>(Task<TValue> task, CancellationToken token)
        {
            if (!token.CanBeCanceled) return await task.ConfigureAwait(false);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return await task.ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            List<Entry> entries;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                entries = new List<Entry>(_entries.Values);
                _entries.Clear();
            }
            var errors = new List<Exception>();
            try { _lifetime.Cancel(); } catch (Exception e) { errors.Add(e); }
            foreach (var entry in entries)
            {
                entry.Completion.TrySetCanceled();
                try { entry.Resource?.Dispose(); } catch (Exception e) { errors.Add(e); }
            }
            _lifetime.Dispose();
            if (errors.Count != 0) throw new AggregateException("Asset cleanup failed.", errors);
        }
    }
}
