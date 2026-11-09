using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Juahn.V2.Assets;
using NUnit.Framework;

public class AssetTests
{
    private sealed class Source : IAssetSource<string>
    {
        public readonly List<TaskCompletionSource<AssetResource<string>>> Loads = new List<TaskCompletionSource<AssetResource<string>>>();
        public CancellationToken Token;
        public int Released;
        public Task<AssetResource<string>> LoadAsync(string key, CancellationToken token)
        {
            Token = token;
            var t = new TaskCompletionSource<AssetResource<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Loads) Loads.Add(t);
            return t.Task;
        }
        public void Complete(int index = 0) => Loads[index].SetResult(new AssetResource<string>("asset", () => Interlocked.Increment(ref Released)));
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++) await Task.Delay(1);
        Assert.That(condition(), Is.True, "Asynchronous cleanup did not finish.");
    }
    [Test] public async Task ConcurrentConsumersShareOneLoadAndReleaseAfterLastLease()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s);
        var a = cache.AcquireAsync("key"); var b = cache.AcquireAsync("key");
        Assert.That(s.Loads.Count, Is.EqualTo(1)); s.Complete();
        using var first = await a; var second = await b;
        first.Dispose(); Assert.That(s.Released, Is.Zero); second.Dispose(); second.Dispose();
        Assert.That(s.Released, Is.EqualTo(1)); Assert.That(cache.EntryCount, Is.Zero);
    }
    [Test] public async Task FirstConsumerCancellationDoesNotCancelSecondOrBackend()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); using var ct = new CancellationTokenSource();
        var a = cache.AcquireAsync("key", ct.Token); var b = cache.AcquireAsync("key"); ct.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await a);
        Assert.That(s.Token.IsCancellationRequested, Is.False); s.Complete(); using var lease = await b;
        Assert.That(lease.Value, Is.EqualTo("asset"));
    }
    [Test] public async Task SecondConsumerCanCancelItsOwnWait()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); using var ct = new CancellationTokenSource();
        var a = cache.AcquireAsync("key"); var b = cache.AcquireAsync("key", ct.Token); ct.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await b); s.Complete(); using var lease = await a;
        Assert.That(s.Loads.Count, Is.EqualTo(1));
    }
    [Test] public async Task AllCanceledWaitersReleaseLateCompletion()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); using var ct = new CancellationTokenSource();
        var a = cache.AcquireAsync("key", ct.Token); ct.Cancel(); Assert.CatchAsync<OperationCanceledException>(async () => await a);
        s.Complete(); await Until(() => s.Released == 1); Assert.That(cache.EntryCount, Is.Zero);
    }
    [Test] public async Task NewWaiterCanJoinAnAbandonedInFlightLoad()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); using var ct = new CancellationTokenSource();
        var a = cache.AcquireAsync("key", ct.Token); ct.Cancel(); Assert.CatchAsync<OperationCanceledException>(async () => await a);
        var b = cache.AcquireAsync("key"); s.Complete(); using var lease = await b;
        Assert.That(s.Loads.Count, Is.EqualTo(1)); Assert.That(s.Released, Is.Zero);
    }
    [Test] public async Task DisposeCancelsWaitersAndReleasesLateBackendResult()
    {
        var s = new Source(); var cache = new AssetCache<string>(s); var a = cache.AcquireAsync("key"); cache.Dispose();
        Assert.CatchAsync<OperationCanceledException>(async () => await a); Assert.That(s.Token.IsCancellationRequested, Is.True);
        s.Complete(); await Until(() => s.Released == 1); cache.Dispose(); Assert.That(s.Released, Is.EqualTo(1));
    }
    [Test] public async Task BackendFailureCanBeRetried()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); var a = cache.AcquireAsync("key");
        s.Loads[0].SetException(new InvalidOperationException("failed")); Assert.ThrowsAsync<InvalidOperationException>(async () => await a);
        var b = cache.AcquireAsync("key"); Assert.That(s.Loads.Count, Is.EqualTo(2)); s.Complete(1); using var lease = await b;
    }
    [Test] public async Task NullBackendResourceIsRejected()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); var a = cache.AcquireAsync("key");
        s.Loads[0].SetResult(null); Assert.ThrowsAsync<InvalidOperationException>(async () => await a); await Task.CompletedTask;
    }
    [Test] public async Task PreCanceledRequestDoesNotStartBackend()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s);
        Assert.CatchAsync<OperationCanceledException>(async () => await cache.AcquireAsync("key", new CancellationToken(true)));
        Assert.That(s.Loads, Is.Empty); await Task.CompletedTask;
    }
    [Test] public async Task DisposedLeaseRejectsValueAccess()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); var a = cache.AcquireAsync("key"); s.Complete(); var lease = await a;
        lease.Dispose(); Assert.Throws<ObjectDisposedException>(() => { _ = lease.Value; });
    }
    [Test] public async Task ProviderDisposeAndLeaseDisposeReleaseOnce()
    {
        var s = new Source(); var cache = new AssetCache<string>(s); var a = cache.AcquireAsync("key"); s.Complete(); var lease = await a;
        cache.Dispose(); lease.Dispose(); Assert.That(s.Released, Is.EqualTo(1));
    }
    [Test] public async Task MultipleScopesRetainResourceIndependently()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); var first = new AssetScope(); var second = new AssetScope();
        var a = first.AcquireAsync(cache, "key"); var b = second.AcquireAsync(cache, "key"); s.Complete(); await Task.WhenAll(a,b);
        first.Dispose(); Assert.That(s.Released, Is.Zero); second.Dispose(); Assert.That(s.Released, Is.EqualTo(1));
    }
    [Test] public async Task ScopeDisposalCancelsPendingAcquisition()
    {
        var s = new Source(); using var cache = new AssetCache<string>(s); var scope = new AssetScope();
        var a = scope.AcquireAsync(cache, "key"); scope.Dispose(); Assert.CatchAsync<OperationCanceledException>(async () => await a);
        s.Complete(); await Until(() => s.Released == 1);
    }
    [Test] public void ClosedScopeImmediatelyDisposesIncomingOwnership()
    {
        var scope = new AssetScope(); scope.Dispose(); var count = 0;
        var lease = new AssetLease<string>("key", "asset", () => count++);
        Assert.Throws<ObjectDisposedException>(() => scope.Own(lease)); Assert.That(count, Is.EqualTo(1));
    }
    [Test] public void ScopeReleasesInReverseOrderEvenWhenCleanupFails()
    {
        var order = new List<int>(); var scope = new AssetScope();
        scope.Own(new AssetLease<string>("a", "a", () => order.Add(1)));
        scope.Own(new AssetLease<string>("b", "b", () => { order.Add(2); throw new Exception(); }));
        Assert.Throws<AggregateException>(() => scope.Dispose()); Assert.That(order, Is.EqualTo(new[]{2,1}));
    }
    [Test] public async Task HundredsOfParallelLeasesStillReleaseExactlyOnce()
    {
        for (var run = 0; run < 20; run++)
        {
            var s = new Source(); using var cache = new AssetCache<string>(s);
            var tasks = Enumerable.Range(0,100).Select(_ => cache.AcquireAsync("key")).ToArray(); s.Complete();
            var leases = await Task.WhenAll(tasks); await Task.WhenAll(leases.Select(x => Task.Run(() => { x.Dispose(); x.Dispose(); })));
            Assert.That(s.Loads.Count, Is.EqualTo(1)); Assert.That(s.Released, Is.EqualTo(1));
        }
    }
}
