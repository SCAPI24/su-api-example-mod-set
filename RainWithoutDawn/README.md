# RainWithoutDawn 冷雨夜

适用于 SuAPI 0.2.1.0，Mod 版本 1.0.0。

保持原版冷雨夜行为：持续启动降水和雾，将昼夜时间维持在午夜附近的原有区间。
降水类型与可见强度仍由原版天气、温度、湿度及世界设置决定。
使用 `UpdateExtension<SubsystemWeather>` 在原版天气更新后执行，保留原版更新；
通过公开接口控制天气和原版 `SubsystemTimeOfDay.TimeOfDayOffset`，不替换任何子系统。
卸载时释放扩展注册。时间偏移仍由原版子系统保存。

使用 .NET SDK 10 和固定版本的 Engine、EntitySystem、Survivalcraft NuGet 包构建：

```powershell
dotnet restore RainWithoutDawn.csproj --source https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json --source https://api.nuget.org/v3/index.json
dotnet build RainWithoutDawn.csproj -c Release --no-restore
```

Release 构建只混淆本 Mod，最终 DLL 为 `bin/Release/net10.0/Obfuscar/RainWithoutDawn.dll`。
Windows 与 Android 使用同一份 DLL。包文件名：`[SuAPI]RainWithoutDawn-1.0.0.scmod`。

```text
ModInfo.xml
Lib/RainWithoutDawn.dll
Content/                 （有资源时）
```

ZIP 条目使用 `/`，不包含外层目录、平台子目录或引擎 DLL。
将 `.scmod` 放入游戏 `Mods/` 目录；仅适用于支持该扩展 API 的 SuAPI 版本。
