# CmdBridgeMod 

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
dotnet build Mod/CmdBridgeMod/CmdBridgeMod.csproj -c Release --framework net8.0
# assembly: bin/Release/net8.0/Obfuscar/CmdBridgeMod.dll  (Release runs Obfuscar via PostBuild)
# package : root ModInfo.xml + flat Lib/CmdBridgeMod.dll, ZIP entries written with forward slashes
```

`IsMergeLib=true`, single `net8.0` assembly at `Lib/CmdBridgeMod.dll` (Windows and Android
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
引擎内 UI 点击（注入前二次校验、不可跳级）、文本输入。
设计与全部实测记录见 `../doc/cmd-bridge-plan.md`。
