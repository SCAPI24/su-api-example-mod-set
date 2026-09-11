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
dotnet build Mod/CmdBridgeMod/CmdBridgeMod.csproj -c Debug --framework net8.0
py -3 Mod/Packages/pack_cmd_bridge.py                     # also deploys to publish/Windows/Mods
```

`IsMergeLib=true`, single `net8.0` assembly at `Lib/CmdBridgeMod.dll` (Windows and Android
share the same DLL). The optional C# client lives in `../CmdBridgeClient/`.

## Files created at runtime

| File | Purpose |
|---|---|
| `<game>/CmdBridge.json` | config (created on first run): port, random token, caps |
| `<game>/CmdBridge.runtime.json` | discovery file: port + token + pid (deleted on unload) |

Loopback-only listener with token authentication; there is no remote attack surface.

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

## Commands

Read-only: `ping` (also reports `dispatcherReady`), `status`, `obs.snapshot`, `obs.ui`,
`obs.player`, `obs.input`, `obs.aim`, `obs.inventory`, `obs.sleep` (inside `obs.player`),
`obs.messages`, `obs.dialogs`, `obs.events`, `obs.world.time`, `obs.world.entities`,
`obs.world.blocks`, `ui.reachability`, `obs.selftest`, `obs.waitFor`.

Actions: `act.look`, `act.lookdelta`, `act.lookat`, `act.key`, `act.hold`, `act.chord`,
`act.mouse`, `act.wheel`, `act.uiclick`, `act.text`, `act.releaseAll`.

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
| `Mod/Packages/pack_cmd_bridge.py` | Python-zipfile `.scmod` packaging + structure validation + deploy |
| `Mod/Packages/smoke_test_cmd_bridge.py` | end-to-end regression against a running game (protocol-level, no client needed) |

## 中文速览

玩家机器人桥：**允许无限查看游戏内数据，但一切交互必须通过玩家控制器**（视角 / 键盘 / 鼠标 / UI 点击），
优势 ≠ 特权。已实测覆盖：界面元素与可达性（含 `hittable` 与 `clickable` 的区分）、虚拟列表项、
玩家状态与按键意图、准星目标、背包、睡眠（含"困到昏睡"与自然回血）、世界方块/实体/时间、
游戏自身提示消息、事件环（一次点击恰好一条事件）、瞬时转视角、按键/鼠标/滚轮注入、
引擎内 UI 点击（注入前二次校验、不可跳级）、文本输入。
设计与全部实测记录见 `../doc/cmd-bridge-plan.md`。
