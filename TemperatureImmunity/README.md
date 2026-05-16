# 压缩包结构
tree
```csharp
    [SuAPI]体温保持（适用于V0116测试）.scmod/
    ├── Lib/
    │   ├── X64/
    │   │   └── TemperatureImmunity.dll
    │   └── Arm64/
    │       └── TemperatureImmunity.dll
    ├── Content/
    │   └── Textures/
    └── ModInfo.xml
```
# 安装位置
tree
```csharp
Application/
├── Survivalcraft.exe
└── Mods/
    └── [SuAPI]体温保持（适用于V0116测试）.scmod/
        ├── Lib/
        │   ├── X64/
        │   │   └── TemperatureImmunity.dll
        │   └── Arm64/
        │       └── TemperatureImmunity.dll
        ├── Content/
        │   └── Textures/
        └── ModInfo.xml
```
# 结构说明
路径	类型	说明
- Lib/X64/TemperatureImmunity.dll	文件	64位系统所需的库文件
- Lib/Arm64/TemperatureImmunity.dll	文件	ARM64系统所需的库文件
- Content/Textures/	目录	纹理资源文件夹
- ModInfo.xml	文件	模组配置文件