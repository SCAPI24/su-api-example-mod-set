# HeadlessRenderingMod

SuAPI-only headless server control for the published Windows Survivalcraft build. The Mod does not patch or replace the game DLLs.

The published `Survivalcraft.exe` starts normally and loads the Mod. The Mod then:

- disables the root Widget draw tree;
- hides the game window after its console is ready;
- supplies 1280x720 OpenTK display metadata when no desktop display is active;
- uses OpenAL Soft's null backend and sets process audio output to zero without changing persistent audio settings;
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
  "maxRequestBytes": 65536
}
```

Each server instance needs its own executable directory, `server.json`, `Settings.xml`, `Worlds/`, `Logs/` and `Scworld/`.

When `disableAudio=true`, the Mod creates `alsoft-headless.ini` beside the executable and points OpenAL Soft at its null backend. No desktop audio device is required.
