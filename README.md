# Juahn V2 Assets

Pure C# / .NET Standard 2.1 / Unity 6000.0+ / MIT. No engine, DI, UI or vendor references.

`AssetCache<T>` combines concurrent loads by key. `AcquireAsync` returns a counted `AssetLease<T>`; dispose the last lease to release the backend resource. Each request can cancel its own wait. Shared backend work uses the provider lifetime token, so one canceled screen does not cancel another screen's request.

```csharp
using var cache = new AssetCache<MyAsset>(mySource);
using var session = new AssetScope();
var lease = await session.AcquireAsync(cache, "menu/background", token);
Use(lease.Value);
```

Use an app/session/screen/pool `AssetScope` to retain or prewarm resources. A pool must retain its prefab lease until every pooled instance is destroyed. Scopes own leases, not providers. Dispose scopes before their provider. Disposing the provider ends its lifetime, cancels waiters, releases loaded resources and releases any late backend completion. Outstanding leases must not be used after provider disposal.

Implement `IAssetSource<T>` and return `AssetResource<T>(value, release)`. Release is exactly once and should not throw. Unity backends must dispatch native operations and release callbacks to the main thread. The optional `com.juahn.v2.assets.addressables` package supplies that implementation.

Run `dotnet test Tests~/dotnet/Tests.csproj`, `dotnet build Tools~/compile-check/Runtime.csproj`, and `python3 Tools~/validate_package.py`. The tests cover cancellation isolation, failed-load retries, delayed completion after disposal, multiple scopes, reverse cleanup and parallel ownership.
