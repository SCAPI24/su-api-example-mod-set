# TemperatureImmunity

体温保持与流感免疫，适配 SuAPI `0.2.1.0`。无需安装其他外部 Mod。

## 多 Mod 共存

- 为原版 `ComponentVitalStats` 和 `ComponentFlu` 注册独立的 `UpdateExtension`，不替换数据库 Class，不修改游戏源码。
- 使用 `base.Update(dt, false)` 截断原版更新本体，其他 Mod 的附加回调仍会执行。
- 食物、体力、睡眠和湿度子逻辑由本 Mod 执行；其他 Mod 的 `base.Update(false)` 只截断宿主本体。需要暂停整个对象时，应使用按所有者隔离的 `SuspendHostUpdates`，同时暂停宿主和全部扩展。
- 体温保持在 `12`。原版温度结算可能在同一帧内造成伤害，因此不能等到原版执行后再修正。
- 按原版顺序调用真实组件的食物、体力、睡眠和湿度方法，仅接管温度部分；死亡时仍停止喘息声。
- 环境温度仍按原版时机采样，保留其他生存机制与存档。只清理温度自己的昏厥状态，不覆盖睡眠或其他 Mod 的共享昏厥效果。
- 流感免疫直接清理真实组件的病程、咳嗽、喷嚏与流感昏厥状态，修复旧实现只清理派生类同名字段的问题。
- 卸载时释放自身注册。同一项状态若被多个 Mod 主动修改，最终效果取决于双方业务逻辑；宿主被完全替换时，SuAPI 不会附加到替换类。

字段与方法在加载时按当前 SDK 绑定；接口不匹配会明确失败，不使用旧版替换路径。

## 编译与打包

使用 .NET SDK 10，目标框架 `net10.0`，精确引用已发布的 `[0.2.1.0]` NuGet 包。

```powershell
dotnet build Mod/TemperatureImmunity/TemperatureImmunity.csproj -c Release
```

Release 只混淆 Mod 自己的 DLL，打包取 `bin/Release/net10.0/Obfuscar/TemperatureImmunity.dll`。

```text
[SuAPI]TemperatureImmunity-1.0.0.scmod
├── ModInfo.xml
├── Lib/TemperatureImmunity.dll
└── Content/
```

`IsMergeLib=true`，DLL 使用扁平 `Lib/`，Windows 与 Android 共用同一份包。
包名只带 Mod 版本，API 版本写在 `ModInfo.xml`。
