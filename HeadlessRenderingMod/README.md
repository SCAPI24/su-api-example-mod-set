# HeadlessRenderingMod

SuAPI-only headless server control for the published Windows Survivalcraft build. The Mod does not patch or replace the game DLLs and does not require any external Mod.

The published `Survivalcraft.exe` starts normally and loads the Mod. The Mod then:

- disables the root Widget draw tree;
- hides the game window after its console is ready;
- supplies 1280x720 OpenTK display metadata when no desktop display is active;
- uses OpenAL Soft's null backend and sets process audio output to zero without changing persistent audio settings;
- embeds Windows OpenGL compatibility libraries and prefers real GL2 entry points before using no-op fallbacks;
- limits the update loop to the configured target rate;
- exposes a local keyboard console for human control;
- exposes authenticated TCP JSON commands for AI control.

## Human console

Double-click `Survivalcraft.exe`. The Mod allocates a visible console before hiding the game window.

The first line of every menu shows the current game screen, for example:

```text
MainMenu> Server Control
```

Controls:

- Up/Down: select an item
- Left/Escape: previous menu
- Right/Enter: next menu or execute
- PageUp/PageDown: change page
- Home/End: first or last item

The main menu supports world creation, joining, listing, export, deletion, player creation and player management. `Command Line` keeps a text command mode for exceptional operations.

## World commands

All commands except `ping` execute on the game thread.

```text
world.create
world.list
world.join       world=<name-or-directory>
world.save
world.close
world.export     world=<name-or-directory> [fileName=name.scworld]
world.delete     world=<name-or-directory>
```

Exports are written to `<Survivalcraft.exe directory>/Scworld/`.

## Player commands

Player commands require a loaded world. Stable player selection uses `playerIndex`.

```text
player.list
player.skin.list [playerClass=Male|Female]
player.create    name=<name> playerClass=Male|Female [skin=$Male1] [enterGame=true]
player.update    playerIndex=<number> [name=<name>] [skin=<skin>]
player.delete    playerIndex=<number>
```

An existing player's class cannot be changed because the game does not allow it after the player is added. Delete and recreate that player to change class.

## AI command sequences

`sequence.start` accepts up to 256 steps and returns immediately. The Mod advances the retained sequence across frames, so one request can span slow world loading without keeping a network connection open.

Step forms:

```json
{"command":"world.join","args":{"world":"ServerWorld"}}
{"waitFor":"world.ready","timeoutSeconds":180}
{"delayMilliseconds":500}
```

Supported wait conditions:

```text
world.loaded
world.unloaded
world.ready
screen.ready
screen:<screen-name>
players.atleast:<count>
```

`status` also returns `screenAnimating`. AI clients should either use a sequence or wait until it is `false` before sending standalone commands that change screens.

Management commands:

```text
sequence.start
sequence.status sequenceId=<id>
sequence.list
sequence.cancel sequenceId=<id>
```

Example:

```text
python serverctl.py direct sequence.start steps='[...]'
python serverctl.py sequence create-world-and-player.sequence.json --wait
```

The second form is recommended. The sample JSON file is deployed beside `serverctl.py`.

## Remote server control

Every target supplies its own SSH address, account and installation root. The
runtime contains no fixed server address or world name. Deploy these tools under
the selected installation root:

```text
C:\SurvivalcraftServer\tools\serverctl.py
C:\SurvivalcraftServer\tools\remote_server_ops.py
```

The `Server Control` main menu does **not** close or take ownership of the TCP
control interface. It is valid for the process to remain on `MainMenu> Server
Control`; automation must use the control API instead of keyboard injection or
asking a user to operate the menu manually.

Run these commands directly on any server, replacing the root when required:

```text
python C:\SurvivalcraftServer\tools\serverctl.py --root C:\SurvivalcraftServer direct ping
python C:\SurvivalcraftServer\tools\serverctl.py --root C:\SurvivalcraftServer direct status
python C:\SurvivalcraftServer\tools\serverctl.py --root C:\SurvivalcraftServer direct world.save
python C:\SurvivalcraftServer\tools\serverctl.py --root C:\SurvivalcraftServer direct world.join world="Jolia Poru"
```

`remote_server_ops.py` supplies process-aware commands whose `status` result also
works while Survivalcraft is stopped:

```text
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer ping
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer status
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer start --world "Jolia Poru"
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer save
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer restart
python C:\SurvivalcraftServer\tools\remote_server_ops.py --root C:\SurvivalcraftServer stop
```

The required recovery order is:

1. Send `ping`. It is handled directly by the control server and does not enter
   the game-thread command queue.
2. Send `status`. Confirm `currentScreen`, `screenAnimating`, `worldLoaded`,
   `queuedCommands`, `serverError`, and `frameError`.
3. When the server is stable at `MainMenu` with no loaded world, send
   `world.join world="Jolia Poru"`.
4. Poll `status` until `currentScreen="Game"`, `worldLoaded=true`,
   `serverError=null`, and `frameError=null`.
5. Confirm `modVersion`, `serverError` and `frameError` in `status`, then inspect
   `[HeadlessRenderingMod]` or error entries in `Logs\Game.log`.

If `ping` succeeds but `status` times out, investigate `Frame.Update` and
`ProcessQueuedCommands`; do not blame the menu or fall back to GUI key presses.
Keep `hideWindow=true`, `disableDrawing=true`, and `enableConsole=true` on this
GPU-less server. Never expose the token in documentation or command output.

## Workstation deployment tools

The scripts under `tools/` are maintained workstation entry points. They contain
no password, token, private configuration, world, signing key, fixed host or
required installation root. Install their workstation dependency once:

```text
py -3 -m pip install -r tools/requirements-remote.txt
```

Omit `--password` to enter the SSH password without showing it in shell history:

```text
py -3 tools/control_remote.py --host <server> --ssh-port 22 --user <ssh-user> --root C:/SurvivalcraftServer status
py -3 tools/control_remote.py --host <server> --user <ssh-user> --root C:/SurvivalcraftServer restart --world "Server World"
py -3 tools/control_remote.py --host <server> --user <ssh-user> --root C:/SurvivalcraftServer direct world.list
py -3 tools/check_remote_runtime.py --host <server> --user <ssh-user> --root C:/SurvivalcraftServer
py -3 tools/deploy_remote_mod.py --host <server> --user <ssh-user> --root C:/SurvivalcraftServer --source <package.scmod>
py -3 tools/deploy_remote_current.py --host <server> --user <ssh-user> --root C:/SurvivalcraftServer --source <windows-publish-directory>
```

`deploy_remote_current.py` stops the server, uploads the selected publish
directory, restarts it, returns to the previously loaded world when known, and
verifies the actual core DLL and `.scmod` hashes found in that directory.

## TCP wire format

One UTF-8 JSON object per line:

```json
{"id":"1","token":"...","command":"status"}
{"id":"2","token":"...","command":"world.list"}
{"id":"3","token":"...","command":"player.list"}
```

Arguments can be placed in `args` or at the request root. The listener accepts numeric loopback addresses only; expose it remotely through a controlled proxy or tunnel.

## Configuration

`server.json` is created beside the executable when absent:

```json
{
  "enabled": true,
  "instanceId": "world-001",
  "bindAddress": "127.0.0.1",
  "port": 26741,
  "token": "at-least-32-characters",
  "targetFrameRate": 20,
  "hideWindow": true,
  "disableDrawing": true,
  "enableConsole": true,
  "disableAudio": true,
  "maxQueuedCommands": 256,
  "maxCommandsPerFrame": 64,
  "requestTimeoutSeconds": 10,
  "maxRequestBytes": 65536,
  "autoApproveDataModificationUserIds": []
}
```

### Auto approving data modification requests

A headless server has nobody to click ScMultiplayer's host approval dialog, so a client mod that
asks the host to modify data stays pending forever. One optional, **mod agnostic** list closes that
gap - it only names user ids, never specific Mods:

| Key | Meaning |
|---|---|
| `autoApproveDataModificationUserIds` | Client **record keys** whose data modification requests are approved automatically, whatever Mod submits them. `"*"` matches every client. An empty list (default) disables auto approval entirely. |

The record key is the account **user id** (`UserManager.ActiveUser.UniqueId`, the GUID stored in the
game's `UserId.dat`); a player cannot change it, unlike the player name. A client without an account
identity is keyed as `name:<player name>` by ScMultiplayer, and such a literal entry is accepted too.

Example - auto approve one account:

```json
"autoApproveDataModificationUserIds": ["42858348-64a8-4ce5-b157-05daf2922fe2"]
```

Behaviour notes:

- Matching is by **case-insensitive record key**. ScMultiplayer's own trusted list
  (`ScMultiplayerTrustedClients.xml`, written by "Always allow this player") stays the in-game way to
  grant the same trust; this setting is the headless convenience layer.
- ScMultiplayer publishes the key with every approval request (`sourceKey`), so the key of an unknown
  client is visible in the log line `[DM] Approval required: ... key=<user id>`.
- The approval is submitted on the next frame through
  `ScMultiplayer.DataModification.ApprovalControl` (`operation=resolve`, `allow=true`), so it never
  re-enters ScMultiplayer's data modification runtime from inside its own event callback.
- Every decision is recorded in three places: `Logs/Game.log` (`[DM] ...`), the console (when no menu
  is open) and the console menu itself. Requests log `[DM] Approval required: ...`; auto approved
  ones additionally log `[DM] Auto approve queued: ...` and `[DM] Auto approve submitted: ...`;
  `ScMultiplayer.DataModification.Result` receipts log `[DM] Result: ...`.
- The console keeps that history under **Multiplayer Hosting > Data modification > Recent decisions
  [N]** (newest first): `AutoApproveQueued` / `AutoApproved` = matched the server.json allowlist,
  `ManualAllowed` / `ManualRejected` = decided in the console menu, `Result` = verdict published by
  ScMultiplayer (host-side verdicts for remote clients are published locally too, so the console shows
  `Applied` / `Failed` / `Rejected` / `Busy` / `Invalid` / `NotSupported` / `Cancelled` with the reason).
- **Multiplayer Hosting > Data modification > Authorised players [N]** answers "who may modify data":
  the `server.json` allowlist, the world trusted list (`ScMultiplayerTrustedClients.xml`, whose requests
  create **no approval at all**), and every online client with its record key plus the verdict
  (`authorised (world trusted list)` / `authorised (server.json allowlist)` / `asks for approval`).
  The screen also explains the active `dataModificationMode`, so `Default` no longer looks like
  "everything is allowed".
- Console output produced while a menu or a text prompt is open is queued and reprinted after the
  interactive screen closes, so the menu can no longer disappear under a background `[DM]` line.
- Manual approval through the console (Multiplayer Hosting > Data modification > Pending approvals)
  and the control command `multiplayer.dm` keep working unchanged.
- Warning: `"*"` approves any joining client. Use it only on a private room.

Each server instance needs its own executable directory, `server.json`, `Settings.xml`, `Worlds/`, `Logs/` and `Scworld/`.

> **Keep `server.json` UTF-8 without a BOM.** The Mod tolerates a BOM, but the Python tooling
> (`tools/remote_server_ops.py`, `tools/serverctl.py`) parses it with a strict `json.load` and fails
> with `Unexpected UTF-8 BOM`. Windows PowerShell 5.1 `Set-Content -Encoding UTF8` adds one - write
> the file with an editor that saves plain UTF-8, or with Python `io.open(path, 'w', encoding='utf-8')`.

When `disableAudio=true`, the Mod creates `alsoft-headless.ini` beside the executable and points OpenAL Soft at its null backend. No desktop audio device is required.

The Windows game publish must provide an `openal32.dll` build whose OpenAL Soft null backend works on Windows Server. The OpenGL fallback DLLs are embedded in `HeadlessRenderingMod.dll`, so building or running the Mod does not require downloading Mesa, llvmpipe or another OpenGL package.
