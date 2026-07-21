# ScMultiplayer

SuAPI multiplayer Mod for Survivalcraft. ScMultiplayer owns room creation, join approval, connected-player management and network synchronization. It does not depend on HeadlessRenderingMod.

## In-world controls

ScMultiplayer controls are added only after a world has loaded. Open the native `More` controls and use:

- `CR`: create a room from the current world. It changes to `IF` after hosting or joining;
- `IF`: list joined player names and current coordinates;
- `TA`: send room chat and immediately show the sent message locally;
- `MP`: open multiplayer room information, recent talk history and host-only management.

No multiplayer management item is added to the main menu or world list.

Clients see `IF`, `TA` and `MP`, but server controls such as join approval, pending requests and disconnecting players are shown only to the host. `MP > Talk` retains the 50 most recent messages for the current room session.

Remote room entries tolerate temporary discovery packet loss and preserve valid selection indices while the list refreshes. Respawning creates a clean authoritative player state, including normal body temperature, dry clothing, full vital stats and cleared transient illness effects.

## Current-world room

Creating a room always performs this native sequence:

1. Save the running project.
2. Unload the project so region files are readable.
3. Export a fresh authoritative room snapshot.
4. Create the Comms room.
5. Reload the same local world.

An older Play-screen snapshot is never reused for this operation.

## Join approval

`MP` includes the persistent `Auto Approve Joins` setting. It can be configured before or after room creation and is stored in `data:/ScMultiplayerSettings.json`.

`Auto Host Current World` is stored in the same file. When enabled, ScMultiplayer creates a room after a world reaches the `Game` screen. If the room disconnects while the world remains loaded, ScMultiplayer retries hosting it.

The same file supports multiple server processes on one machine:

```json
{
  "serverBasePort": 51459,
  "serverPortCount": 64,
  "serverPreferredPort": 51459
}
```

Each process first binds its optional `serverPreferredPort`, then falls back to the remaining locally available ports in the range beginning at `serverBasePort`. Assigning a different preferred port to each persistent instance prevents simultaneous starts from exchanging room ports. Discovery still scans the complete consecutive range, so all rooms on one IP are listed. A port used on another IP does not affect local allocation.

- On: valid requests are accepted immediately.
- Off: every request, including a previously known player identity, requires a host decision.

Manual decisions are:

- Allow Join
- Reject Join
- Later

`Later` keeps the request in `Pending Join Requests`. The host can reopen it from `MP`. Pending transport handshakes remain valid for up to five minutes and are rejected shortly before expiry.

## Connected players

While hosting, `MP` shows connected remote players with their player name, client ID and endpoint. Selecting a player allows the host to disconnect that client. Player records remain stored so disconnecting a player does not erase their world progress.
