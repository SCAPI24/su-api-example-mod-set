# CmdBridgeMod 

## 0.2.1.0 迁移状态（2026-11-11）

当前工程使用 .NET SDK 10 / net10.0，正式 CNB NuGet 精确 `[0.2.1.0]`，清单 APIVersion=0.2.1.0；自身版本与 IsMergeLib=true 保持。本文后续的历史编译、自检数字不代表本轮结果。本轮尚待主会话串行 restore、Release 编译/混淆、包检查及真实游戏宿主验收；不新增独立测试工程。

详见 [本轮迁移记录](doc/migration-021.md)。


A player-robot bridge for Survivalcraft 2 (SuAPI). It exposes the game to a local
command-line client / AI so the game can be *played* through the **player controller**
— never through state cheats.

> **Rule of the mod: infinite reading, but every interaction goes through the player controller.**
> Advantage (all-seeing reads, superhuman input speed) is allowed. Privilege (writing game
> state directly) is not. See `../doc/cmd-bridge-plan.md` §1 for the full boundary.

## What it does

| Layer | Capability |
|---|---|
| Read (unlimited) | UI elements with coordinates and reachability, player state, current key intent, crosshair target, inventory, sleep state, world blocks/entities/time, the game's own on-screen messages, and an event ring |
| Act (player controller only) | instant look / look-at-coordinates / look-delta, key press/hold/chord, mouse down/up/click, wheel, engine-internal UI clicks, text typing |

It does **not** replace any Subsystem/Component/Parameter, does not register Injector
mappings, and does not touch rendering, window, audio or frame rate — so a human can keep
playing normally while the bridge drives the same character.

## Install

Build and pack with the repository tooling, then drop the `.scmod` into the game's `Mods/`:

```bash
dotnet build Mod/CmdBridgeMod/CmdBridgeMod.csproj -c Release --framework net10.0
# assembly: bin/Release/net10.0/Obfuscar/CmdBridgeMod.dll  (Release runs the ObfuscateMod target)
# package : root ModInfo.xml + flat Lib/CmdBridgeMod.dll, ZIP entries written with forward slashes
```

`IsMergeLib=true`, single `net10.0` assembly at `Lib/CmdBridgeMod.dll` (Windows and Android
share the same DLL). The optional C# client lives in `../CmdBridgeClient/`.

> The former `Mod/Packages/pack_cmd_bridge.py` helper is not part of this checkout; pack the two
> entries above directly and keep the flat `Lib/` layout rule in mind (no `Lib/X64`, `Lib/Arm64`).

## Files created at runtime

| File | Purpose |
|---|---|
| `<game>/CmdBridge.json` | config (created on first run): port, random token, caps |
| `<game>/CmdBridge.runtime.json` | discovery file: port + token + pid (deleted on unload) |

Loopback-only listener with token authentication; there is no remote attack surface.

## Several game instances on one machine

`CmdBridge.json`'s `port` is a **preference**, not a hard requirement: when it is already taken
(typically by a second instance of the game started from another directory), the mod moves to the
next free port for that run and logs

```text
[CmdBridge] port 26751 is already in use (another game instance?); instance "win-x64" uses port 26752 for this run.
```

Before this fallback existed the whole mod failed to load (`Failed to load mod CmdBridgeMod: …`).
The effective port always lands in `<game>/CmdBridge.runtime.json`, so every instance stays
discoverable; the config file itself is never rewritten.

`CmdBridge.runtime.json` is written with an atomic replace, and a hard kill can still leave a stale
file behind — clients must validate its `pid` against the live process (the bundled `sccmd` does).

## How the injection works

The bridge remains a global service; it does not register a `ScreenExtension` or replace a
native update host. `Frame.Update` observes completed UI input and signals the next frame-start
pump. The independent `CursorSoftGuard` stays at order -11, immediately before `ComponentInput`.
Moving these operations into a screen lifecycle would change their required timing.

The frame service also runs when the TCP listener is disabled, so dependent Mods can still use
the input facade. Unload removes only this Mod's event subscription and cursor guard, invalidates
queued frame-start batches, stops late input producers, and restores natural input state.


All commands run on the **game thread at the start of the next frame**, using
`Dispatcher.Dispatch` from the background server thread. That is the only seam where a
synthetic input pulse survives: `Keyboard.AfterFrame` / `Mouse.AfterFrame` clear the
`downOnce` arrays at the end of each frame, and `Frame.Update` (used for observation) fires
*before* that cleanup.

Only these members are ever written — everything else is read-only
(enforced by `Mod/Packages/check_cmd_bridge_readonly.py`):

```text
Keyboard.m_keysDownArray / m_keysDownOnceArray / m_keysDownRepeatArray / m_lastKey / m_lastChar
Mouse.m_mouseButtonsDownArray / m_mouseButtonsDownOnceArray / MouseWheelMovement / m_lastMouseWheelValue
ComponentLocomotion.m_lookAngles        (view pitch; yaw is written through ComponentBody.Rotation)
WidgetInput.m_mouseDownPoint            (cleared after a synthetic click, defensively)
```

There is exactly one more (UI) write path, added in 1.1.29: the **pre-input UI check** closes the
UI that makes the engine ignore world input. It only calls the engine's own close/accept paths —
`MessageDialog.Dismiss(...)` (the code behind OK/Cancel), `TextBoxDialog.Dismiss(...)`,
`DialogsManager.HideDialog(...)`, `ComponentGui.ModalPanelWidget = null` and
`ButtonWidget.IsChecked = false` for the HUD `MoreButton`. It never writes world state
(no teleport, no inventory, no blocks) and never touches the real mouse/keyboard/window.
See `doc/cmdbridge-ui-automation.md` section 14.

## Commands

Read-only: `ping` (also reports `dispatcherReady`), `status`, `obs.snapshot`, `obs.ui`,
`obs.player`, `players.all` / `obs.players` (every `PlayerData`, see below), `obs.input`,
`obs.aim`, `obs.inventory`, `obs.sleep` (inside `obs.player`),
`obs.messages`, `obs.dialogs`, `obs.events`, `obs.world.time`, `obs.world.entities`,
`obs.world.blocks`, `ui.reachability`, `obs.selftest`, `obs.waitFor`.

Debug overlay (draws in-world, memory only): `box.block`, `box.area`, `box.list`, `box.clear`,
`box.remove` — see "Drawing boxes on the world" below.

Actions: `act.look`, `act.lookdelta`, `act.lookat`, `act.key`, `act.hold`, `act.chord`,
`act.mouse`, `act.wheel`, `act.uiclick`, `act.text`, `act.releaseAll`.

Blocking UI: `act.closeui` dismisses the UI that is eating world input (dialog → its own
accept/cancel path, modal panel → `ModalPanelWidget = null`, HUD `MoreContents` → collapse)
and reports `dismissedUi` / `dismissedCount` / `dismissedByKind`. Every world-input command
(`act.move/lookat/key/dig/use/interact/place/aim`) runs the same check before injecting;
if a blocker cannot be dismissed the command fails with `ui_blocking` instead of injecting a
silent no-op.

Player interaction (the engine's right mouse button, injected virtually):
`act.use` (held item `OnUse`), `act.interact` (block `OnInteract`), `act.place` (placement) —
all three are the same `PlayerInput.Interact` edge, and `ComponentPlayer.Update` runs
`Use -> Interact -> Place` in that order (`ComponentPlayer.cs:151-181`);
`act.aim start|release|cancel` holds / releases / cancels `PlayerInput.Aim`
(`ComponentInput.cs:185`, `ComponentPlayer.cs:183-227`). See
`doc/cmdbridge-ui-automation.md` section 13 for the full mapping and caveats.

## Drawing boxes on the world: `box.*`

Debug helper: draw a **fixed-coordinate** wireframe box around a block or an area so you can
see with your own eyes where a coordinate actually is.

```
box block x y z [--color <name|RRGGBB|AARRGGBB>]
box area  x1 y1 z1 x2 y2 z2 [--color <name|RRGGBB|AARRGGBB>]
box list
box clear
box remove <id>
```

- **Fixed coordinates.** Boxes stay where you put them; they do not follow the block and the
  draw path never reads terrain.
- **Memory only.** Nothing is written to the save or to any file; a restart clears everything.
- Default colour is green (`40FF60FF`); `box list` echoes the cell ranges and the colour.
- **Colour may be a name** taken from the engine's own palette (`Engine/Color.cs`), so you do
  not have to type RGB. Matching is case-insensitive and ignores `_` / `-` / spaces
  (`dark_green`, `Dark-Green` and `darkgreen` are the same). Hex still works.

  ```
  black gray white red green yellow blue cyan magenta
  darkgray darkred darkgreen darkyellow darkblue darkcyan darkmagenta
  lightgray lightred lightgreen lightyellow lightblue lightcyan lightmagenta
  orange pink chartreuse violet mintgreen skyblue brown purple olive indigo
  mutedgreen inkblue
  ```

  `box block -76 65 -101 --color red` and `box block -76 65 -101 --color FF0000` are equivalent.
- `box area` accepts the two corners in any order (each axis is sorted to min/max).
- Cap is 256 simultaneous boxes; exceeding it fails with `overlay_limit` rather than silently
  dropping.

### Why it does not interfere with other mods

| Layer | Isolation |
|---|---|
| Data | Private static list inside `Server/BridgeOverlayState.cs`; `internal` types, unreachable from other mods |
| Draw registration | `SubsystemDrawing.AddDrawable(IDrawable)` (public API). **No** subsystem replacement, **no** `Pak/Database.xml` node, **no** injector |
| Draw order | `2001` — distinct from CircuitAutoRouter `100`, MiniMap `1246`, ScMultiplayer territory overlay `2000`, engine block highlight `1`/`2000` |
| Lifetime | The drawable is registered only while at least one box exists; zero boxes = nothing added to the world, zero overhead. `OnUnload` detaches and clears |

**Do not attach the drawable as an entity component.** `Entity` has no public `AddComponent`,
and `Project` has no component-add API either (`Project.cs` only exposes `CreateEntity` /
`AddEntity` / `RemoveEntity`); adding a component to a live entity means rebuilding its
`ValuesDictionary`. `subsystemDrawing.AddDrawable` is the supported path.

## Pitfalls learned the hard way

### 1. The packaged DLL must come from `Obfuscar/`, not from the plain `bin` folder

`CmdBridgeMod.csproj` runs Obfuscar after a successful Release build:

```
InPath  = $(TargetDir)
OutPath = $(TargetDir)Obfuscar
```

So `bin/Release/net10.0/CmdBridgeMod.dll` is the **pre-obfuscation** intermediate artifact and
`bin/Release/net10.0/Obfuscar/CmdBridgeMod.dll` is the one that actually ships. They have
different sizes and hashes and **both look plausible**, so picking the wrong one silently
produces a package that loads an older-looking build.

Observed symptom: the new command is absent at runtime, `status` still reports the new
`modVersion` (both files carry it), and no error is logged anywhere — the package simply
contains the wrong assembly.

Rule: after `dotnet build -c Release`, always package
`bin/Release/<tfm>/Obfuscar/<Assembly>.dll`, and verify the packaged hash equals the
**Obfuscar** output:

```bash
python3 - <<'EOF'
import zipfile, hashlib
src = 'bin/Release/net10.0/Obfuscar/CmdBridgeMod.dll'
with zipfile.ZipFile('[SuAPI]CmdBridgeMod-x.y.z.scmod') as z:
    inner = z.read('Lib/CmdBridgeMod.dll')
print(hashlib.sha256(open(src,'rb').read()).hexdigest() ==
      hashlib.sha256(inner).hexdigest())
EOF
```

### 2. Grepping a packed/obfuscated assembly for command strings proves nothing

Obfuscar stores string literals in a compressed/encrypted form, so **no** command name —
not even a long-standing one like `obs.world.blocks` — appears as ASCII or UTF-16 in the output
DLL. A search that finds nothing is not evidence that the command is missing.

To check whether a command is really registered, ask the running game:

```
sccmd raw box.block x=-76 y=65 z=-101     # reaches the server even for brand-new commands
sccmd commands                            # full list from `cmd.list`
```

`sccmd raw <command> key=value ...` bypasses the client's own command table, so it is the
authoritative test for "did my server-side command register".

### 3. Adding a server command also requires updating the `sccmd` client

`CmdBridgeClient/Program.cs` dispatches on a hard-coded `switch`, so a new `box.*` command is
invisible to `sccmd` until the client is rebuilt — the server side is fine, but `sccmd box.block`
reports `Unknown command`. Either extend the client's `switch`, or test with `raw`.

### 4. `Engine.Color(uint)` is little-endian ARGB, not `0xRRGGBB`

`Color(uint packedValue)` stores the value straight into `PackedValue`, and the packing order is
**little-endian ARGB** — `(a << 24) | (b << 16) | (g << 8) | r` (`Engine/Color.cs:142`, matching
`Color(byte r, byte g, byte b, byte a)`).

So `new Color(0xFF000000u | 0xFF3030u)` produces `R=0x30, G=0x30, B=0xFF` — **red and blue are
swapped**. Parsing `--color` must shift out the bytes and use the four-argument constructor:

```csharp
byte a = len == 8 ? (byte)((raw >> 24) & 0xFF) : (byte)0xFF;
byte r = (byte)((raw >> 16) & 0xFF);
byte g = (byte)((raw >> 8)  & 0xFF);
byte b = (byte) (raw        & 0xFF);
return new Color(r, g, b, a);
```

Symptom to watch for: `--color FF3030` comes back as `3030FF` in `box list`.

### 5. `adb push` of a package path with brackets needs care

Inside Git Bash the argument can be path-translated and the push lands somewhere unintended
(`remote secure_mkdirs() failed`). Push the **absolute** path and quote the destination:

```bash
adb -s "$SERIAL" push "P:/.../[SuAPI]CmdBridgeMod-1.1.39.scmod" /sdcard/Download/Survivalcraft/Mods/
```

Then compare hashes on both ends before restarting the game — a silent partial deploy looks
exactly like a code bug.

## All players: `players.all` / `obs.players`

`obs.player` only reports `SubsystemPlayers.ComponentPlayers[0]`. This command instead merges
**three engine collections**, because no single one contains every character:

- `SubsystemPlayers.PlayersData` — host split-screen H1/H2/H3, including a `PlayerData` whose
  body has not spawned yet.
- `SubsystemPlayers.ComponentPlayers` — characters that currently have a `ComponentPlayer`.
  The current SuMultiplayer removes the remote avatar's `PlayerData` from `m_playersData` and
  keeps only its `ComponentPlayer` in `m_componentPlayers`
  (`Mod/SuMultiplayer/Engine/NetworkAvatarSpawner.cs`, the “remote avatar has no UI” block).
- `SubsystemBodies.Bodies` — every body-bearing entity. The remote avatar's entity is removed
  from `Project.m_entities` directly (without `OnEntityRemoved`), so its `ComponentBody` stays
  here; engine bookkeeping (`UpdateComponentPlayers`) can also drop the remote from
  `ComponentPlayers` when any other entity is added/removed. Scanning bodies by their
  `ComponentPlayer` is what keeps the joined client visible in that state.

The merge is deduped by `PlayerData` reference and sorted by `playerIndex`. Remote avatars are
still `PlayerData`; only their `GameWidget` has been removed, so they must not be looked up
through `PlayerData.GameWidget` (that getter throws or returns a stale cached widget).
Local/remote is decided by reverse-matching `GameWidget.PlayerData` in
`SubsystemGameWidgets.GameWidgets`. Every entry reports `inPlayersData` /
`inComponentPlayers` / `inBodies` so the engine representation is visible; a live SuMP host
can show the client avatar as `inPlayersData:false`, `inComponentPlayers:false`,
`inBodies:true`.

The result is a **JSON array**, one entry per player character:

```text
sccmd players [--index N] [--max N] [--slots N]
raw: players.all { index?: -1, max?: 0, slots?: 16 }
```

| Argument | Meaning |
|---|---|
| `index` | `-1` (default) = every player; otherwise only that `playerIndex` (engine-assigned, **not** the split-screen slot — use `isLocal` + `gameWidgetIndex` for H1/H2/H3). Unknown index → `player_not_found` (with the valid indices in the message). |
| `max` | `0` (default) = all; otherwise at most N entries. Hard cap = 32 (engine `MaxPlayers` is 4). |
| `slots` | per-inventory non-empty slot details returned; default 16, hard cap 64. `0` returns only the counts. |

Each entry contains identity (`playerIndex`, `name`, `playerClass`, `isLocal`,
`inPlayersData`, `inComponentPlayers`, `inBodies`, `gameWidgetIndex`, `hasEntity`,
`hasComponentPlayer`, `playerState`, …), position/velocity/
rotation, look (`yawDeg` from body rotation, `pitchDeg` + `headYawDeg` from
`ComponentLocomotion.LookAngles`, eye position/rotation from `ComponentCreatureModel`),
health (`health`, `air`, `airCapacity`, `isDead`, `causeOfDeath`, …), sleep, on-fire,
flu/sickness, vitals (food/stamina/sleep/temperature/wetness/satiation), inventory
(`slotsCount`, `activeSlot`, `holding`, bounded `slots`), and action hints (`aiming`,
mining/dig progress, attack power, model motion state). Missing components or a disposed
entity never throw: `stateRead`/`stateReason` mark the entry and `errors` maps each failed
field to its exception text. A local `PlayerData` whose body has not spawned yet is still
listed with `hasComponentPlayer:false` / `hasEntity:false`; a remote avatar appears with
`inPlayersData:false` / `inBodies:true` (`inComponentPlayers` may be false).

```jsonc
{
  "playerIndex": 1, "name": "Basil", "playerClass": "male", "isLocal": true,
  "inPlayersData": true, "inComponentPlayers": true, "inBodies": true,
  "gameWidgetIndex": 1, "hasEntity": true, "hasComponentPlayer": true,
  "playerState": "Playing", "stateRead": true, "stateReason": null,
  "position": {"x":198.6,"y":65.0,"z":98.3},
  "look": {"yawDeg":-120.7,"pitchDeg":21.4,"headYawDeg":0.0},
  "health": {"health":1.0,"air":1.0,"airCapacity":1.0,"breathingMode":"Air"},
  "isDead": false,
  "sleep": {"isSleeping":false,"sleepFactor":0.0},
  "fire": {"isOnFire":false,"touchesFire":false},
  "flu": {"hasFlu":false,"isCoughing":false},
  "sickness": {"isSick":false,"isPuking":false},
  "vitals": {"food":0.07,"stamina":1.0,"sleep":0.30,"temperature":10.8,
             "wetness":0.0,"satiation":{"total":0.4,"count":1,"entries":[...]}},
  "inventory": {"slotsCount":26,"activeSlot":0,"holding":{...},
                "slots":[{"slot":1,"value":162,"contents":162,"data":0,"count":1}],
                "slotsNonEmpty":1,"slotsReturned":1,"slotsOmitted":0,"slotLimit":16},
  "action": {"aiming":false,"digging":false,"digProgress":0.0,"digTime":0.0,
             "attackPower":0.0,"movementAnimationPhase":1.2,"deathPhase":0.0},
  "errors": {}
}
```

Wire format is one UTF-8 JSON object per line:

```json
{"id":"1","token":"...","command":"obs.snapshot","args":{"maxElements":256}}
{"id":"1","ok":true,"result":{...}}
```

## Reading the UI: `hittable` vs `clickable`

This distinction matters, and it is the code-level reason the game wants you to use keys:

- `hittable` — `Widget.HitTestGlobal(center)` resolves to this element (geometry).
- `clickable` — the element's input hierarchy will actually **derive** a click.

In the world, `ComponentInput` sets `IsMouseCursorVisible = false` (the mouse is handed to
the camera, `ComponentInput.cs:155`) and the whole `Press/Tap/Click` derivation is gated by
that flag (`WidgetInput.cs:741`). So **in-world HUD buttons are hit-testable but not
clickable** — a human uses keys there. Opening a modal panel/dialog restores the cursor
(`ComponentInput.cs:143-152`) and the same elements become clickable.

```jsonc
// in world
{"name":"InventoryButton","hittable":true,"clickable":false,
 "clickReason":"mouse cursor is captured (IsMouseCursorVisible=false); use keys instead"}
// after pressing e (inventory open)
{"type":"InventorySlotWidget","hittable":true,"clickable":true,"clickReason":null}
```

**AI rule: click only when `clickable == true`; otherwise use keys.**

## Waiting: `obs.waitFor`

Screen switches, world loads and panel openings take several frames: `SwitchScreen` updates
`CurrentScreen` immediately, but the new screen's widgets only enter `RootWidget.Children`
partway through the transition (`ScreensManager.cs:85, 302-309`). Clicking during a transition
is refused with `screen_busy` on purpose. So **never sleep and guess - wait for a condition**:

```text
obs.waitFor  condition=screen.animating.false            timeoutMs=10000
obs.waitFor  condition=element.present:WorldsList
obs.waitFor  condition=element.clickable:SleepButton
obs.waitFor  condition=modal.is:FullInventoryWidget
obs.waitFor  conditions=["player.alive","modal.none"]
```

Conditions: `screen.animating.false|true`, `screen.is:<name>`, `element.present|hittable|clickable:<selector>`,
`modal.none`, `modal.is:<TypeName>`, `dialog.none`, `dialog.present`, `world.loaded`, `world.unloaded`,
`player.alive`, `player.dead`, `player.sleeping`, `player.awake`, `events.since:<seq>`.
The result reports `satisfied`, `elapsedMs`, `polls` and, on timeout, `pendingCondition`.

Canonical click pattern: `waitFor screen.animating.false` -> `act.uiclick` -> `waitFor` the effect.

## Event ring

`obs.events` is an incrementally pulled ring (`sinceSeq`). Kinds:

`ui.click` (semantic button click, edge-deduplicated), `ui.tap` (raw click on any interactive
widget — inventory slots, sliders, list rows, text boxes), `ui.message` (the game's own
on-screen hints, e.g. "You will faint, go to sleep!", "Too uncomfortable to sleep"),
`world.dig/hit/interact/aim`, `screen.changed`, `modal.opened/closed`, `dialog.shown/hidden`,
`world.loaded/unloaded`, `player.damaged/healed/died/slotChanged/sleepStarted/sleepEnded`.

One click command produces **exactly one** `ui.click` / `ui.tap` event: `ScreensManager.SwitchScreen`
freezes the widget tree during transitions (`ScreensManager.cs:81`), which used to make a
single click repeat for the whole animation.

## Recipes (player-controller only, no privileges)

```text
Sleep (there is no sleep key — it is a two-step UI action)
  1. obs.player.sleep.canSleep / secondsUntilFaint
  2. move onto sleepable ground (Grass 1.0; Dirt/Sand/logs/leaves/planks 0.5; stone 0)
  3. key c  → waitFor modal.is:ClothingWidget
  4. re-check canSleep → click SleepButton
  5. wait for event player.sleepStarted (allowManualWakeUp=true means a chosen sleep)
      · wake early: wait >= ~5 s (SleepFactor must reach 1) then send any input
      · manual wake needs 10 game-seconds of sleep; a collapse (allowManualWakeUp=false) ignores input

Inventory / character panel
  key e → modal.opened FullInventoryWidget → obs.ui(inventory slots, clickable=true) → act.uiclick
  key c → modal.opened ClothingWidget (clothing slots, VitalStatsButton, SleepButton)

Eat / drink (no shortcut — use the normal item flow)
  select the food with digit keys or the hotbar, aim at nothing, act.mouse right

Mine / place / interact
  act.lookat <cell> → act.mouse left down (dig, hold) / act.mouse right click (place/interact)

Hotbar
  act.wheel <notches> (negative = forward) or number keys
```

## Verification tooling

| Tool | Purpose |
|---|---|
| `Mod/Packages/check_cmd_bridge_readonly.py` | static audit: no state writes, every write goes through `InputWhitelist` |
| `Mod/Packages/pack_cmd_bridge.py` | `.scmod` packaging + structure validation + deploy |
| `Mod/Packages/smoke_test_cmd_bridge.py` | end-to-end regression against a running game (protocol-level, no client needed) |

## 中文速览

玩家机器人桥：**允许无限查看游戏内数据，但一切交互必须通过玩家控制器**（视角 / 键盘 / 鼠标 / UI 点击），
优势 ≠ 特权。已实测覆盖：界面元素与可达性（含 `hittable` 与 `clickable` 的区分）、虚拟列表项、
玩家状态与按键意图、准星目标、背包、睡眠（含"困到昏睡"与自然回血）、世界方块/实体/时间、
游戏自身提示消息、事件环（一次点击恰好一条事件）、瞬时转视角、按键/鼠标/滚轮注入、
引擎内 UI 点击（注入前二次校验、不可跳级）、文本输入、世界内固定坐标线框（`box.*`，纯内存、
DrawOrder 2001，与其他 Mod 的绘制互不干涉）。
设计与全部实测记录见 `../doc/cmd-bridge-plan.md`。

Core dependencies are exact NuGet references to `[0.2.0.0]` (SDK 10, `net10.0`).
The Release-only `ObfuscateMod` target resolves dependencies from MSBuild's package assets
in `obj/Release/net10.0/ObfuscarDependencies` and processes only the Mod's own DLL.
