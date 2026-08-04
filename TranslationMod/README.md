# TranslationMod

`TranslationMod` translates fixed original-game and SuAPI built-in UI text. It also provides an opt-in API for external Mods. An external Mod is never scanned unless it explicitly registers a UI root or asks to translate a string.

## Built-in Scope

- Fixed original game screens and SuAPI built-in screens are translated and may export unknown fixed text.
- Community download entries, game log entries, world names, player names, world seeds, sign text, and game HUD are excluded.
- The original export is `Logs/zh_CN.xml`.
- On Android, it is `/sdcard/Download/Survivalcraft/Logs/zh_CN.xml`.
- Export I/O is asynchronous at startup and is not performed when a menu becomes idle.

## External Mod API

Reference `TranslationMod.dll` and declare it as a merge-library dependency in the external Mod's `ModInfo.xml`:

```xml
<Dependency>
  <ModInfo>
    <Identifier>TranslationMod</Identifier>
    <IsMergeLib>true</IsMergeLib>
  </ModInfo>
</Dependency>
```

Create one context when the Mod loads. It carries the Mod ID and language, so every later call stays short:

```csharp
using TranslationMod;

private static readonly TranslationContext T = TranslationApi.For("ExampleMod");
```

Register the root after it is attached to the screen. All standard visible text below it is covered, including labels, buttons, checkboxes, sliders, links, and message widgets. Text assigned later is also processed. The scanner reads only the registered root and does not use reflection.

```csharp
T.RegisterWidget(myDialogOrPanel);

// Only needed when the root is permanently disposed before the Mod unloads.
T.UnregisterWidget(myDialogOrPanel);
```

For self-drawn text, status lines, dynamically created messages, or any non-standard control, use the same context directly:

```csharp
label.Text = T.Text("Connecting to host...", "ConnectionStatus");
messageWidget.DisplayMessage(T.Text("Download complete", "Download"), Color.White, false);
string status = T.Format("Connected to {0}", endpoint);
```

`SetText` also chooses the matching Chinese font automatically for standard controls:

```csharp
T.SetText(button, "Join room");
T.SetText(checkBox, "Enable relay");
T.SetText(slider, "View distance");
T.SetText(link, "Open project page");
```

Use `Text` for fixed text and `Format` for text with numbers, player names, hosts, or other changing values. The untranslated template is exported once; the changing values do not generate duplicate entries. User-created content such as chat, names, seeds, signs, and text-box input should remain raw and must not be passed to this API.

## Shipped Translation Tables

Unknown entries are exported per Mod and language when TranslationMod unloads:

```
Logs/
  Translations/
    ExampleMod.zh_CN.xml
```

To ship completed translations, add a uniquely named XML file to the external Mod's own `Content` directory, load it through `ContentCache`, and register it before building the UI. Use the Mod ID in the content name so different Mods cannot collide in SuAPI's shared content cache.

```csharp
using Engine.Content;
using System.Xml.Linq;

T.AddTranslations(ContentCache.Get<XElement>(
    "Mod/Translations/ExampleMod.zh_CN", false));
```

The XML format matches the exported table:

```xml
<Translations>
  <Screen Name="ConnectionStatus">
    <Entry Original="Connecting to host..." Translation="正在连接主机..." />
  </Screen>
</Translations>
```

The local file in `Logs/Translations` is loaded first. It therefore overrides a translation bundled with the Mod, allowing a translator to update a released Mod without rebuilding it.

## Packaging

The package uses the required merge-library layout:

```
[SuAPI]TranslationMod-1.6.3.scmod
|- ModInfo.xml
|- Lib/TranslationMod.dll
`- Content/zh_CN.xml
```

Build the project before packaging. Use the final obfuscated DLL at `bin/Debug/net8.0/Obfuscar/TranslationMod.dll`; do not package Engine, Survivalcraft, or EntitySystem DLLs.
