# Design sources and compatibility

Checked 2026-11-09. These modules independently implement ownership/lifecycle/interface patterns identified in reference analysis. Proprietary game source, assets, packet contracts and table schemas are not included.

| Source | Version checked | License / reuse | Compatibility and limits |
| --- | --- | --- | --- |
| [Unity Addressables memory management](https://docs.unity3d.com/Packages/com.unity.addressables@2.9/manual/MemoryManagement.html) | 2.9.1 | [Unity Companion License](https://docs.unity3d.com/Packages/com.unity.addressables@2.9/license/LICENSE.html); referenced SDK, no copied SDK source | Matches validation consumer; handles require explicit release; release does not imply instant bundle unloading |
| [UniTask](https://github.com/Cysharp/UniTask) | local consumer 2.5.11 | MIT; existing UI dependency | Only the UI bridge uses UniTask. Pure modules use .NET Standard 2.1 Task contracts |
| [VContainer](https://github.com/hadashiA/VContainer/tree/1.19.0) | 1.19.0 | MIT; existing optional bridge | Existing scope ownership remains authoritative; module host does not replace the DI container |
| [Unity package manifests](https://docs.unity3d.com/6000.0/Documentation/Manual/upm-manifestPkg.html) | Unity 6000.0 | Documentation reference | Declare each Git package in consumer manifest; optional vendor dependencies are explicit |
| [TaskCompletionSource](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcompletionsource-1?view=netstandard-2.1) | .NET Standard 2.1 | BCL API reference | Shared completion plus separate cancellation registrations supports independent waiters |

Alternatives considered: integrating Addressables into UiService minimizes package count but forces a backend dependency on all consumers; wrapping every operation in a global manager obscures ownership. Independent source/core/adapter packages preserve the existing optional-bridge design. Module startup uses an explicit ordered list; automatic dependency discovery is deferred until a concrete project requires it.

Research and execution are separate. Executed checks are recorded in the validation document: pure-core regression tests, .NET Standard compilation, Unity adapter compilation, isolated Unity PlayMode and package-boundary validation. This does not verify external accounts, purchases, advertising services or a remote content deployment.
