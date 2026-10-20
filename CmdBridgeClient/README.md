# sccmd — CmdBridge console client

A tiny .NET 8 console program that talks to **CmdBridgeMod** over the mod's loopback JSON
protocol. It contains no game code: it discovers the running game, sends commands, and
prints results (human table or `--json`).

```bash
dotnet build Mod/CmdBridgeClient/CmdBridgeClient.csproj -c Debug
Mod/CmdBridgeClient/bin/Debug/net8.0/sccmd.exe status
```

## Discovery

`sccmd` finds the game process named `Survivalcraft`, reads `CmdBridge.runtime.json` from
the game directory, and uses the port/token from there — no configuration needed. Override
with `--root <game dir>`, `--port <n>`, `--token <t>`.

With **several instances running at once**, `--root` is required: without it `sccmd` reports
`instance_ambiguous` (exit code 4) and lists every candidate (root, pid, instanceId, port) instead
of silently picking one. A `runtime.json` whose `pid` is not the live process is ignored as stale.

## Commands

```text
# read
sccmd status                          screen / window / animation / layout state
sccmd ui [--all] [--filter T] [--max N]    UI elements with coordinates and reachability
sccmd reachability                    which elements cannot be reached, and what blocks them
sccmd player                          player state, key intent, inventory, sleep (first local player only)
sccmd players [--index N] [--max N] [--slots N]
                                      every player character as a JSON array: host split-screen
                                      H1/H2/H3 plus the remote avatar (if any), merged from
                                      PlayersData + ComponentPlayers; bounded slots
sccmd input                           raw engine input state (also diagnoses stuck input)
sccmd aim [maxDistance]               what the crosshair points at
sccmd messages                        the game's own on-screen hints and overlays
sccmd dialogs                         open dialogs
sccmd events [sinceSeq]               event-ring delta
sccmd world time|entities|blocks      world observation
sccmd snapshot                        everything in one request
sccmd selftest                        injection points + reachability self-check
sccmd waitfor <condition> [--timeout ms]   server-side condition wait (exit code 7 on timeout)

# act (player controller only)
sccmd look <yawDeg> <pitchDeg>        instant absolute view
sccmd lookdelta <dYaw> <dPitch>       relative view
sccmd lookat <x> <y> <z>              look at a world position (solved against engine math)
sccmd key <name> [holdMs]             key pulse (default holdMs = 40 ms ≈ one frame; explicit 0 = press+release in the same frame)
sccmd hold <name> / release <name>|--all
sccmd chord ctrl v                    modifier chord (same 40 ms default for the target key)
sccmd mouse left click|down|up        world dig / attack (left button). Place/interact/use/aim are the
                                      right button and have their own commands below
sccmd wheel <notches>                 hotbar / list scrolling (negative = forward)
sccmd act.use [--hold ms]             right-click edge -> ComponentMiner.Use (held item OnUse: bucket,
                                      boat, whistle, match, hammer, rake, saddle, fertilizer)
sccmd act.interact [--hold ms]        right-click edge -> ComponentMiner.Interact (chest, workbench,
                                      door, switch, button, sign, furnace, ...)
sccmd act.place [--hold ms]           right-click edge -> ComponentMiner.Place (place the held block)
sccmd act.aim start [--hold ms]       hold the right button = PlayerInput.Aim (draw/aim); with --hold it
                                      auto-releases after ms (= fire)
sccmd act.aim release                 release -> AimState.Completed (fire/throw)
sccmd act.aim cancel                  lower without firing (AimState.Cancelled + clear pending aim)
sccmd closeui [--cancel]              close the blocking UI now: MessageDialog -> its own accept (Button1)
                                      / cancel (Button2) path, other Dialog -> DialogsManager.HideDialog,
                                      inventory/creative/container panel -> ComponentGui.ModalPanelWidget = null,
                                      HUD MoreContents -> MoreButton.IsChecked = false.
                                      Every world-input command (act.move/lookat/key/dig/use/interact/place/aim)
                                      runs this check automatically and reports dismissedUi; if a blocker
                                      cannot be dismissed the command fails with ui_blocking instead of
                                      injecting a silent no-op.
sccmd click <selector|id> [--at x y]  engine-internal UI click (re-validated before injecting)
sccmd text <string>                   type into the *focused* text box, one char per frame;
                                      no focus -> no_focused_textbox, no room -> text_rejected
sccmd settext <selector> <text>       replace a TextBoxWidget's whole text through its own
                                      Text property (fires TextChanged, so the dialog's internal
                                      state follows). Use this for EditMemoryBankDialog.Line0..F /
                                      LinearText and EditTruthTableDialog.LinearText.
sccmd accept [dialog] [--button <sel>] accept the current (or named) dialog: a synthetic Tap+Click
                                      on its OK button runs the dialog's own IsClicked commit
                                      branch (Dismiss alone would not commit)
sccmd dialog ok|cancel [dialog]      same as accept / the dialog's own Cancel button
sccmd raw <command> k=v ...           any protocol command

# interactive
sccmd                                 REPL: ui / click 3 / hold w / lookat 12 68 -30 / quit
```

Global options: `--json`, `--root`, `--port`, `--token`, `--timeout <ms>`. `sccmd help`
works offline.

## Comparing host characters with the joined client: `sccmd players`

`player` only prints `SubsystemPlayers.ComponentPlayers[0]`. `players` asks the server for
`players.all`, which returns a **JSON array with one entry per player character**: H1/H2/H3 on
the host and the remote avatar of a joined client. The server merges `PlayersData` (host local,
including not-yet-spawned characters), `ComponentPlayers`, and `SubsystemBodies.Bodies` (the
remote avatar's `PlayerData` is removed from `PlayersData`, and engine bookkeeping can also
drop it from `ComponentPlayers`, but its body stays in `SubsystemBodies`). Entries are deduped
by `PlayerData`. Remote avatars have no `GameWidget` (the multiplayer layer removes it), so
`isLocal:false` identifies them; `inPlayersData` / `inComponentPlayers` / `inBodies` show which
engine collection supplied the entry, and `hasComponentPlayer` / `hasEntity` distinguish "data
exists but body not spawned yet" from a live body.

```bash
sccmd --json players                       # compact JSON array, full state
sccmd players                              # indented JSON
sccmd players --index 0                    # only playerIndex 0 (not the split-screen slot)
sccmd players --max 2 --slots 4            # first two entries, at most 4 inventory slots each
```

Compare entries by `playerIndex` + `isLocal`: host H1/H2/H3 are the `isLocal:true` entries
(their `gameWidgetIndex` is the split-screen slot), the joined client is the `isLocal:false`
entry (typically `inPlayersData:false` + `inBodies:true`; `inComponentPlayers` may be false
after engine bookkeeping).
`health`, `look`, `position`, `sleep`, `vitals`, `inventory.slots` are directly
comparable. `errors` is an object (empty when everything was readable); a disposed entity is
reported with `stateRead:false` + `stateReason` instead of throwing.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | generic failure / unknown command |
| 2 | `element_missing` — the selector does not exist on the current screen |
| 3 | `element_occluded` / `ambiguous_selector` — exists but not clickable right now, or `ui_modal_open` / `ui_blocking` (a modal/dialog eats the world input, or it could not be dismissed) |
| 4 | discovery / connection problem (`instance_ambiguous` when several instances run and `--root` is missing) |
| 5 | `screen_busy` (transition in progress) or `layout_invalid` / `window_not_active` / `world_not_active` / `input_channel_unavailable` or timeout |
| 6 | `world_not_loaded` / `player_not_found` / `player_not_ready` (not ready, dead or sleeping) |
| 7 | `waitfor` timed out - the condition is reported in `pendingCondition` |

Codes 2 and 3 are the ones an AI must distinguish: 2 means "this step does not exist yet"
(do not skip levels), 3 means "it exists but something is in the way or the mouse is
captured" (use keys, scroll, or close the covering dialog first).

## 中文速览

`sccmd` 是 CmdBridgeMod 的命令行客户端（纯 BCL，零游戏引用）：自动发现运行中的游戏并读取端口/token，
支持一次性命令与交互式 REPL，`--json` 便于脚本/AI 解析，退出码区分"元素不存在"（2）与"存在但点不到"（3）。
观察面只读；动作面只走玩家控制器（视角/键盘/鼠标/UI 点击/文本），不做任何状态修改。
`sccmd players` 一次列出**全部** `PlayerData`（主机分屏 H1/H2/H3 + 联机远端 avatar），
`--json` 返回数组原文；`player` 仍然只报第一个本地角色。
