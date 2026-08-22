# Control Animal Mod

## Enter and Exit Control

1. Press `Shift` to crouch.
2. Move within 2.5 blocks of a living animal.
3. Hold `R` or the fixed `R` HUD button for two seconds.
4. Press `R` or the HUD button again to return to the player.

## Movement and Camera

- `W/S`: move along the animal body direction.
- `A/D`: turn the animal body. Android uses horizontal movement input.
- Mouse or Android look input turns only the animal head.
- Head yaw is limited to approximately ±140 degrees relative to the body.
- Turning the body recenters the head.

## Bird Flight

- A bird must use `ComponentBirdModel` and have `FlySpeed > 0` to fly.
- While controlling such a bird, the native fly button is visible in every game mode.
- Press the fly button or `F` to enable bird flight.
- With flight disabled, tapping jump performs the bird's native upward jump.
- With flight enabled, every desktop or Android jump tap grants a `0.70` second upward lift pulse; holding jump is not required.
- Between jump taps, enabled flight uses zero vertical order and clears residual downward velocity to hold altitude as closely as possible.
- `W/S` controls forward/back flight.
- Descent begins only after flight is disabled, using `-0.30` vertical order until the bird lands.
- During descent, tapping jump starts a `0.35` second upward pulse to slow the slide; holding is not required.
- During descent, `W/S` still moves along the bird body direction and `A/D` still turns the body, so landing is not vertical-only.
- Bird flight uses the bird's native `FlyOrder` and does not enable player creative flight.
- Native `ComponentPilot` and `ComponentPathfinding` updates are suspended while controlled, preventing them from applying hidden automatic flight when bird flight is disabled.

## Animal Skill

- While controlling an animal, the native crouch button and `Shift` become the animal skill.
- The current skill plays the animal's native idle call.
- The hidden player is not crouched by this input.

## Attack and Feeding

- Click the left mouse button or tap on Android to attack a player or animal within about 1.75 blocks.
- First-person and third-person attacks both originate at the controlled animal's eye position and follow its current head direction.
- Damage occurs at the native attack animation hit moment, with a short fallback for bird pecking.
- Hold the left mouse button or Android view input near grass or meat to feed.
- Birds retain their native `FeedOrder` pecking animation while feeding.

## Egg Laying

- Birds with a valid native `EggType` show an independent `EGG` button.
- Right click or press `EGG` while the bird is standing on the ground.
- The laying action takes three seconds and creates one native laid egg.
- The egg type, spawn position, velocity, and `Audio/EggLaid` sound reuse the native game behavior.
- Egg laying does not replace left-button feeding.

## Animal Stats Panel

- Press `C` or the native clothing/character button to open the animal panel.
- Press the same input again to close it.
- The clothing/character button is highlighted; the inventory button remains off.
- Labels use the native English terminology and a two-column vertical layout: left `Health`, `Food`, `Stamina`; right `Speed`, `Attack`, `Position`.

## Implementation Boundary

This is a Mod-only control-target switch. The native `ComponentPlayer` remains the input, UI, save, and lifecycle host; the controlled animal is not registered as a second native player.
