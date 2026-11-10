# CircuitAutoRouter

Build with .NET SDK 10: `dotnet build CircuitAutoRouter.csproj -c Release`.
Engine, EntitySystem and Survivalcraft are pinned to the published NuGet SDK 0.2.1.0.
Release obfuscates only CircuitAutoRouter.dll, using resolved SDK/framework references.
The packaging DLL is `bin/Release/net10.0/Obfuscar/CircuitAutoRouter.dll`.

The routing subsystem is independently appended to the game database. It does not
replace vanilla subsystems, components, screens, or another Mod's shared host.
The rod inventory editor opens the existing router panel; routing algorithms are unchanged.
Event subscriptions are removed individually when this Mod unloads.

Package as `[SuAPI]CircuitAutoRouter-1.0.0.scmod`, with `ModInfo.xml` at the ZIP root
and the obfuscated DLL in flat `Lib/`. Keep `IsMergeLib=true`.
