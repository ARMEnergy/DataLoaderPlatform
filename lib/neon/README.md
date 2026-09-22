# Neon SDK (Marex) — vendor binaries

`NeonMarkets.NeonAPI` **3.15.1.22**, authored by Spectron Services (Marex). These three
assemblies are not on any NuGet feed, so they are vendored here and referenced by
`src/DataLoader.Marex/DataLoader.Marex.csproj` via `<Reference><HintPath>`.

| Assembly | What's in it |
|---|---|
| `Neon.API.dll` | `INeonApiClient` / `NeonApiClient`, Auth0 token acquisition, connection state |
| `SignalRClient.dll` | the SignalR transport plus every request/response/snapshot/update DTO (`SignalRClient.Contracts`) |
| `Common.Core.dll` | domain enums (`Common.Core.Enums`), `BoolResult<T>`, entity DTOs |

## They are .NET Framework 4.6.1 assemblies and they run fine on .NET 8

The vendor's own integration note assumed otherwise and recommended building a separate
net48 connector process. That was tested and is **not** necessary — verified live on
2026-09-21: `dotnet --version` 9.0.304, runtime 8.0.20, full Auth0 token + SignalR
websocket handshake + all six snapshots received. Two things make it work:

1. Every referenced package has a `netstandard2.0` (or lower) target —
   `Microsoft.AspNet.SignalR.Client` 2.4.3 included — so the .NET 8 loader resolves them
   normally. The exact versions are pinned in the loader `.csproj`.
2. `Common.Core.dll` additionally references **AutoMapper, Unity and Topshelf**, none of
   which have usable .NET 8 targets. They are never restored and never need to be,
   because .NET resolves assembly references **lazily**: the loader only touches
   `Common.Core.Utility.BoolResult<T>` and `Common.Core.Enums.*`, and no code path from
   there reaches a type in those three assemblies. If a future change starts using a
   different part of `Common.Core`, a `FileNotFoundException` for one of them at run time
   is the expected symptom — do not "fix" it by adding the packages blindly, check what
   new type was touched.

`System.Configuration`, `mscorlib`, `System.Core`, `System.Xml` and `System.Net.Http` all
resolve through the facades shipped in the .NET 8 shared framework.

## Upgrading

The live gateway reports `minVersion` **3.37.0.1522** while this SDK is **3.15.1.22**, and
the client has an `IncompatibleVersion` state — but the server accepts 3.15 today
(verified live). If Marex ships a newer package, drop the new assemblies in here and
re-run `tests/DataLoader.Marex.Tests` — `MarexSdkContractTests` reflects over these DLLs
and fails if a member the loader depends on has moved.
