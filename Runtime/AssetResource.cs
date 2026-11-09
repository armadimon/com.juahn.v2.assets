using System;
using System.Threading;
using System.Threading.Tasks;

namespace Juahn.V2.Assets
{
    /// <summary>A backend load and its exactly-once release. The cache owns this object.</summary>
    public sealed class AssetResource<T> : IDisposable where T : class
    {
        private Action _release;
        public T Value { get; }
        public AssetResource(T value, Action release)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public interface IAssetSource<T> where T : class
    {
        /// <summary>The token belongs to the provider, never to an individual consumer.</summary>
        Task<AssetResource<T>> LoadAsync(string key, CancellationToken cancellationToken);
    }

    public interface IAssetProvider<T> where T : class
    {
        Task<AssetLease<T>> AcquireAsync(string key, CancellationToken cancellationToken = default);
    }

    /// <summary>One consumer's ownership. Retain this lease while using any derived instances.</summary>
    public sealed class AssetLease<T> : IDisposable where T : class
    {
        private Action _release;
        private readonly T _value;
        public string Key { get; }
        public T Value => Volatile.Read(ref _release) != null
            ? _value : throw new ObjectDisposedException(nameof(AssetLease<T>));
        public AssetLease(string key, T value, Action release)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
