# TranslationMod

`TranslationMod` translates fixed original-game and SuAPI built-in UI text. It also provides an opt-in API for external Mods. An external Mod is never scanned unless it explicitly registers a UI root or asks to translate a string.

## Built-in Scope

The single rule is: **anything the player can customise is not translated; every other piece of UI is.**

- The original export is `Logs/zh_CN.xml`; on Android it is `/sdcard/Download/Survivalcraft/Logs/zh_CN.xml`.
- Export I/O is asynchronous at startup and is not performed when a menu becomes idle.
- Scanned UI assemblies: the game itself, `SuAPICore`, `SuAPIModDownload`, `SuAPIExternalContentImport`,
  and `ScMultiplayer` (this repository's own Mods). Third-party Mods are still never scanned unless they
  opt in through the API below.

### What is excluded, and why

A `ListPanelWidget` row is **not** excluded as a whole. A row mixes player text with
game-authored text — `CommunityContentItem.Text` holds the community content name while
`CommunityContentItem.Details` holds `World 34KB` and `CommunityContentItem.ExtraText` holds
`(862K downloads, 2016/12/17, v2.0)`. Excluding the screen's whole list (which this Mod used to
do) left those two game-authored labels permanently English and they were never even collected
into the export table. The exclusion is therefore applied per string and per label:

| Carrier | Covers |
| --- | --- |
| Row **name** labels: `CommunityContentItem.Text`, `ExternalContentItem.Text`, `FurniturePackItem.Text` | The row's own title in community, external-content and content-management lists. |
| The string equals a player-supplied field of the row's own item object (`WorldInfo.WorldSettings.Name` / `DirectoryName`, `CommunityContentEntry.Name` / `Url`, `ExternalContentEntry`'s `Storage.GetFileName(Path)`, `PlayerInfo.CharacterSkinName`, `PlayerData.Name`) | World names, player names, community content names, imported file names. `ListPanelWidget` tags every row it builds with the item object, so the comparison needs no per-screen knowledge. |
| A list panel named in `DynamicContentLists` | The game log viewer (`ViewGameLogDialog.ListPanel`) — rows are raw log lines that grow with debugging. |
| A `MessageWidget` subtree | Chat and transient on-screen messages, whose text comes from players or from other Mods. |
| A UI root registered through `TranslationApi` | External Mods that opted in. |

`CommunityContentEntry.ExtraText` is deliberately *not* in that list: it is a fixed format
published by the community server, not something a player writes, so it is translated and
collected like any other game-authored string.

Everything else is translatable, including the in-game HUD, the `GameScreen`, vanilla dialogs
such as `GameMenuDialog`, `EditSignDialog`, `EditTruthTableDialog` and `EditMemoryBankDialog`,
the world-list metadata line (size / date / player count / game mode / environment), the sort and
content-type filter dialogs, and the `SuAPI Mod | <version> | Installed` line in content
management. Pure data rows (truth-table `0`/`1` lines, the `0-9A-F` hex keypad) simply never match
a table entry.

### Lookup order

`TranslationProcessor.Process` tries the following in order and returns the first hit:

| # | Step | Handles |
| --- | --- | --- |
| 1 | exact table match | ordinary strings |
| 2 | trailing-colon title | `GameMenuDialog.AddStat` rows (`title + ":"`) — must run **before** templates, or `"{0} days"` swallows the colon (`"Day 41:"` once rendered as `"第 41: 天"`) |
| 3 | reverse template (`{0} recipes`) | formatted strings; captured groups are looked up in the table again, with a case-insensitive fallback (`bitten by a gray wolf` → `Gray Wolf`) |
| 4 | line-wise | multi-line property/lore blocks; strips per-line leading/trailing spaces before lookup |
| 5 | single line, trimmed | right-aligned one-liners such as `string.Format("{0,24}", "TOTAL")` |
| 6 | `" \| "` segments | composite info rows such as `193KB \| 15 Sep 2026 21:25 \| 1 player \| Creative \| Living` |
| 7 | `"<label> <data size>"` | community content row details such as `World 1.2MB` |

Anything still unmatched is written to the export table unless it came from a `MessageWidget`
or an opted-in external Mod.

### Export Table Version

The export carries a `Version` attribute on its root element:

```xml
<Translations Version="1.6.31.3128.ae1f834e">
```

The value is `<Mod version>.<entry count>.<table hash>` of the bundled `Content/zh_CN.xml`.
At startup the Mod compares it against the current bundled table and, when it differs — including
when the attribute is absent or the file cannot be parsed at all — **silently deletes the export and
re-seeds it from the bundled table**. No dialog is shown and nothing is asked of the user.

Only the version attribute is inspected; a mismatch is never merged or repaired.
A matching version means the file is kept as-is, so translator work collected on top of the
current table survives restarts.

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
[SuAPI]TranslationMod-1.6.49.scmod
|- ModInfo.xml
|- Lib/TranslationMod.dll
`- Content/zh_CN.xml
```

Build the project before packaging. Use the final obfuscated DLL at `bin/Debug/net8.0/Obfuscar/TranslationMod.dll`; do not package Engine, Survivalcraft, or EntitySystem DLLs.
