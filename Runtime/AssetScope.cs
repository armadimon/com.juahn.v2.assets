using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Juahn.V2.Assets
{
    /// <summary>Explicit app/session/screen/pool ownership. Scopes own leases, not providers.</summary>
    public sealed class AssetScope : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<IDisposable> _leases = new List<IDisposable>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly CancellationToken _token;
        private bool _disposed;
        public AssetScope() { _token = _lifetime.Token; }
        public TLease Own<TLease>(TLease lease) where TLease : class, IDisposable
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            lock (_gate)
            {
                if (!_disposed) { _leases.Add(lease); return lease; }
            }
            lease.Dispose();
            throw new ObjectDisposedException(nameof(AssetScope));
        }
        public async Task<AssetLease<T>> AcquireAsync<T>(IAssetProvider<T> provider, string key, CancellationToken token = default) where T : class
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, token))
                return Own(await provider.AcquireAsync(key, linked.Token).ConfigureAwait(false));
        }
        public void Dispose()
        {
            IDisposable[] leases;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                leases = _leases.ToArray();
                _leases.Clear();
            }
            var errors = new List<Exception>();
            try { _lifetime.Cancel(); } catch (Exception e) { errors.Add(e); }
            for (var i = leases.Length - 1; i >= 0; i--)
                try { leases[i].Dispose(); } catch (Exception e) { errors.Add(e); }
            _lifetime.Dispose();
            if (errors.Count != 0) throw new AggregateException("Asset scope cleanup failed.", errors);
        }
    }
}
