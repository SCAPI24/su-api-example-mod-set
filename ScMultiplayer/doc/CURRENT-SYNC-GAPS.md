# ScMultiplayer Current Synchronization Gaps

> Verified against the current `ScMultiplayer` source and the reference
> `SurvivalcraftNet` source on 2026-08-18.
>
> This document is the authoritative list of known synchronization gaps. Older
> comparison and requirement documents describe historical implementation states
> and must not be used as current status without checking the code again.

## Scope

The following reference-project features are intentionally out of scope and are
not ScMultiplayer tasks:

- team/group management;
- territory permission management.

Do not report those two features as missing synchronization work unless the scope
is explicitly changed.

## Confirmed Partial Or Missing Coverage

### General moving-block lifecycle

Current coverage:

- piston moving sets are published and applied by `WorldObjectSynchronizer`;
- authoritative terrain recovery removes some stale local collapsing-block
  predictions.

Gap:

- there is no general network lifecycle for every `IMovingBlockSet` equivalent to
  the reference project's Add, Remove and Stopped events;
- non-piston moving sets therefore rely on final terrain convergence rather than
  an authoritative moving-set lifecycle and continuous presentation.

Relevant current code:

- `Func/WorldObjectSynchronizer.cs:902-982` filters and reconstructs only sets
  whose ID is `Piston`;
- `Modules/Session/ScMultiplayerClientEvents.cs:1857-1905` removes selected local
  collapsing predictions but does not provide a generic moving-block protocol.

### General body impulses and body-to-body collisions

Current coverage:

- player damage knockback has an authoritative sequence, velocity and stun
  correction;
- animal, mount and projectile bodies receive authoritative transform snapshots.

Gap:

- there is no general message for arbitrary `ComponentBody.ApplyImpulse` events;
- there is no general message for body-to-body collision callbacks or their
  one-shot resulting impulse;
- transform snapshots eventually correct position and velocity, but do not
  preserve every collision event or immediate local presentation.

Do not treat ordinary damage knockback as proof that all body impulses are
covered.

### Player ladder state

Current coverage:

- player position, velocity, look angles, movement, jump, crouch, flight and mount
  state are synchronized.

Gap:

- `GamePlayerInputMessage` and `GamePlayerPositionMessage` do not carry
  `ComponentLocomotion.LadderValue` or an equivalent ladder attachment state;
- ladder movement may converge through position snapshots, but the attachment
  and animation state have no explicit authority edge.

Relevant current code:

- `Message/GamePlayerInputMessage.cs:9-28`;
- `Message/GamePlayerPositionMessage.cs:8-35`.

### Block-fire lifecycle metadata

Current coverage:

- terrain cell changes produced by fire are host-authoritative and distributed
  through terrain synchronization;
- `SuSubsystemFireBlockBehavior` prevents clients from running persistent random
  burn and spread logic;
- fire ambience is calculated locally from authoritative fire cells;
- player `ComponentOnFire` duration is synchronized separately.

Gap:

- there is no explicit per-cell fire Add/Remove message carrying the host's fire
  expansion or duration metadata;
- clients receive the resulting terrain state, but do not receive the reference
  project's exact block-fire timer lifecycle.

This is a metadata and presentation gap, not evidence that final burned terrain
is unsynchronized.

### Flu and sickness transient effects

Current coverage:

- flu duration, sickness duration, cough sequence and coughing state are included
  in the authoritative player-health message;
- cough presentation is applied by `SuComponentFlu`.

Gap:

- flu onset, sneeze duration and an explicit sneeze event are not synchronized;
- the reference project's explicit FluEffect and StartFlu events have no direct
  equivalents;
- sickness nausea/puke presentation has no explicit authoritative event.

Relevant current code:

- `Message/GamePlayerHealthMessage.cs:41-45`;
- `Modules/Player/ScMultiplayerHealthWorldControlHandlers.cs:377-392`;
- `Func/Component/SuComponentFlu.cs:46-53`.

### Fine-grained animal behavior and sound events

Current coverage:

- active behavior/state-machine name, target entity, herd, attack order, feed
  order, transforms and health are synchronized;
- idle, attack and howl sound events are synchronized;
- damage sequences reproduce pain sounds.

Gap:

- there are no dedicated authority fields for the reference project's `IsDigIn`
  and `IsBend` behavior edges;
- rowing is represented for players, but there is no generic animal-behavior
  event equivalent to the reference package;
- moan, sneeze, cough and puke are not represented in `AnimalSoundType` as
  independent animal sound events.

Some of these presentations may follow from the synchronized active state
machine. They remain partial until runtime verification proves equivalent timing
and effects.

Relevant current code:

- `Message/AnimalSoundMessage.cs:7-12`;
- `Modules/World/ScMultiplayerWorldSync.cs:732-795` and `963-1008`;
- `Modules/Network/ScMultiplayerMessageHandlers.cs:928-1056`.

### Complete PlayerStats replication

Current coverage:

- the host updates gameplay counters for host-authoritative actions such as
  blocks dug, blocks placed and furniture made;
- level is synchronized and persisted with the network player record.

Gap:

- there is no general network serialization of the complete `PlayerStats` object;
- statistics not naturally produced by host-authoritative execution can differ
  between peers or be absent from the host record.

This gap does not include level, health, inventory or survival attributes; those
already have dedicated authority paths.

### Furniture-set operation semantics

Current coverage:

- furniture designs and sets are transferred through compressed authoritative
  snapshots;
- client-created designs can be merged by the host;
- furniture construction uses a dedicated host-validated request.

Gap:

- client-side furniture-set deletion, rename, ordering and reassignment do not
  have dedicated request/result operations equivalent to the reference project's
  New/Delete/Rename/Move/AddToSet events;
- snapshot merging should not be assumed to preserve the intent and validation of
  every individual set-management operation.

Relevant current code:

- `Func/WorldObjectSynchronizer.cs:405-647`;
- `Modules/World/ScMultiplayerFurnitureHandlers.cs:104-253`.

## Already Covered: Do Not Reopen From Historical Documents

The current implementation has equivalent or stronger paths for the following:

- player movement, look, actions, health, survival attributes, death and respawn;
- exact mount identity plus reliable mount/dismount actions and results;
- player inventory, clothing, chests, furnaces, dispensers and crafting tables;
- terrain changes, chunk checkpoints and recovery;
- projectiles, projectile hits and removal;
- explosions and resulting authoritative terrain changes;
- pickable creation, movement, collection and removal;
- animal lifecycle, transforms, primary behavior state, targets and health;
- circuit input, state snapshots, fences and recovery;
- editable circuit data, including memory banks, truth tables, delays, switches,
  buttons, pistons and dispenser settings;
- furniture design construction, furniture snapshots and sign snapshots/edits;
- world transfer, time, weather, fog, lightning and world-control requests;
- player profiles, clothing and custom skin assets;
- sleep/wake state and host-authoritative time acceleration.

Before changing this list, trace the message registration, sender, router and
receiver in the current source. The presence of an old `待实现` entry is not
sufficient evidence of a current gap.
