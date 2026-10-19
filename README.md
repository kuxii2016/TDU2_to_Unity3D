# TDU2 to Unity3D

**A Unity3D port of Test Drive Unlimited 2 — bringing the original game world, features, and experience into Unity with custom code and new possibilities.**

TDU2 to Unity3D is a personal project dedicated to recreating and porting the world and gameplay systems of *Test Drive Unlimited 2* into the Unity3D engine.

The goal is to bring as much of the original game's functionality as possible into Unity, including its maps, vehicles, interiors, racing activities, radio system, weather, time cycle, and online multiplayer features.

This project is more than just a map viewer or a simple graphics port. It is an ongoing attempt to rebuild the systems that make TDU2 what it is, using custom code and a more flexible foundation for future development.

## Why This Project?

Why put so much work into recreating an existing game?

The answer is simple: **having control over the code opens up possibilities that would otherwise be difficult or impossible to achieve.**

By implementing game systems independently in Unity, the project can be extended, modified, and experimented with in ways that are not possible when working exclusively within the original game's architecture.

The long-term vision is to recreate as much of the original TDU2 experience as possible while providing a flexible foundation for new features, improvements, and custom content.

This is a work in progress, and the project is still evolving.

## Current Development Status

A significant amount of functionality has already been implemented. The following features are currently available to varying degrees of completion.

### World and Map Loading

- **Hawaii** — map loading and world rendering.
- **Ibiza** — map loading and world rendering.
- **Casino** — map loading and world rendering.
- Map switching between Hawaii, Ibiza, and the Casino.
- Road and terrain loading.
- Vegetation rendering.
- Collision loading for supported world objects.
- Animated textures.
- Traffic vehicle spawning and movement.

The objective is to recreate the original game environments using the original game data while handling the world through Unity's rendering and runtime systems.

### Vehicles and Driving

- Loading vehicle data from the original game's databases and files.
- Loading vehicle models and associated resources.
- Vehicle dirt and dirt accumulation systems.
- Vehicle spawning and management.
- Garage arrival vehicle functionality.
- Multiplayer synchronization of vehicle states.

The vehicle implementation is still under development. Differences between the original game engine and Unity's physics can affect vehicle handling, collisions, and gameplay mechanics.

### Interiors and Buildings

- Loading interior resources.
- Partial implementation of houses and garages.
- Support for selected interior areas and building components.
- Ongoing work to improve interior loading and functionality.

Not every interior is fully functional yet. Some locations may be incomplete, contain incorrectly positioned objects, or lack certain original interactions.

### Racing and Activities

- Racing event support.
- Driving school functionality.
- Support for selected racing activities and game modes.
- Recovery of certain previously discarded or unused game features.

**Important:** Some races, cups, and driving school challenges may be extremely difficult or impossible to complete due to differences in vehicle physics, handling, and gameplay behavior compared with the original game.

### Original Game UI and Screens

- Support for selected original Flash-based screens.
- Implementation of selected original game interfaces.
- Sticker Shop functionality.
- Restoration of certain previously discarded features.

The goal is to preserve as much of the original game's look and feel as possible while adapting its systems to the Unity environment.

### Radio and Audio Systems

- Original TDU2 radio functionality.
- **Kalua Radio** support for custom music.
- Radio synchronization in multiplayer.
- Radio messages related to weather and time of day.

The radio implementation aims to preserve the atmosphere of the original game while allowing additional customization through custom music and extended functionality.

### Day/Night Cycle and Weather

- Dynamic time-of-day changes.
- Day/night cycle support.
- Weather system.
- Weather synchronization between multiplayer players.
- Time synchronization between multiplayer players.
- Radio messages associated with changing weather conditions and time of day.

These systems help recreate the changing atmosphere of the original game world and keep the experience consistent between connected players.

### Online Multiplayer

Multiplayer functionality is one of the major components of the project.

Currently implemented or supported systems include:

- Online connectivity.
- In-game chat.
- Player synchronization.
- Multiplayer vehicle synchronization.
- Vehicle position and rotation synchronization.
- Vehicle light synchronization.
- Wheel synchronization.
- Vehicle paint and color synchronization.
- Sticker synchronization.
- Weather synchronization.
- Time-of-day synchronization.
- Traffic synchronization.
- Traffic light synchronization.
- Radio synchronization.

The intention is to allow multiple players to share the same recreated game world while synchronizing important gameplay and environmental states.

Multiplayer functionality is still under development, and synchronization quality may vary depending on the feature and current implementation.

## Restored and Reimplemented Features

In addition to the main systems, this project brings back selected features that were previously discarded or left unused during the original game's development.

These features are being investigated, restored, or adapted to work within the Unity implementation.

The exact availability and completeness of individual features may vary between builds.

## Requirements

To use TDU2 to Unity3D, you must have a copy of the original *Test Drive Unlimited 2* game.

**The original game files are not included in this repository.**

You must unpack the original game files before using the port. The required resources must then be copied into the appropriate Shared Assets directory used by the Unity project.

This includes:

- The unpacked game assets and resource files.
- The required original game data.
- The `.cpr` files.
- The `.xmb` files located in the original game's root directory.

The exact directory structure must match what the current build expects. Missing files, incorrect directory structures, or incomplete extraction may prevent the game world and other resources from loading correctly.

### Setup

1. Obtain and install the original *Test Drive Unlimited 2* game.
2. Unpack the game's resource files using an appropriate extraction tool.
3. Locate the Shared Assets directory used by the Unity port.
4. Copy the required unpacked game files into that directory.
5. Make sure the required `.cpr` and `.xmb` files from the original game's root directory are included in the expected locations.
6. Launch the Unity build and check the logs if any resources fail to load.

**Note:** This is a general setup overview. The exact folder layout and any additional requirements may change as development progresses. Refer to the repository's current instructions and configuration for build-specific details.

## Known Limitations and Warnings

TDU2 to Unity3D is still an experimental, unfinished project.

Although many systems are already implemented, the port is not yet a complete or fully accurate recreation of the original game.

You may encounter:

- Missing or incorrectly loaded assets.
- Visual glitches and rendering differences.
- Incorrect object positions or collisions.
- Incomplete interiors and interactions.
- Vehicle handling differences.
- Physics-related issues during races and driving school challenges.
- Incomplete or inconsistent multiplayer synchronization.
- Broken or partially implemented gameplay features.
- Crashes, unexpected behavior, or other runtime errors.

The original game was built around its own engine, physics, and gameplay systems. Recreating these systems in Unity introduces differences that cannot always be resolved simply by loading the original assets.

**Do not expect every feature to work exactly as it does in the original game.** Some systems are experimental, and others may require further implementation or significant rework.

## Project Goals

The ongoing development goals include:

- Improving the stability of world and asset loading.
- Increasing compatibility across Hawaii, Ibiza, and the Casino.
- Improving vehicle physics and driving behavior.
- Completing more interiors and interactive locations.
- Expanding racing and driving school compatibility.
- Improving multiplayer synchronization and reliability.
- Restoring additional original game features.
- Refining the radio, weather, and time-of-day systems.
- Fixing bugs and improving overall performance.
- Making the codebase easier to extend and maintain.

The ultimate goal is to bring as much of the original TDU2 experience as possible into Unity while retaining the freedom to develop new features using custom code.

## Disclaimer

TDU2 to Unity3D is an independent fan-made project. It is not an official port, remake, or product of Eden Games or Atari.

*Test Drive Unlimited 2*, its original game assets, trademarks, and other associated intellectual property belong to their respective rights holders.

This project does not distribute the original game's assets. Users must obtain and unpack their own copy of the game.

## Final Words

TDU2 to Unity3D is an ongoing personal development project built around one simple idea:

**What if Test Drive Unlimited 2 could be rebuilt in Unity, with custom code and the freedom to expand its functionality?**

There is still a lot of work to do, but the foundations are already in place: multiple playable environments, vehicle loading, world systems, racing activities, radio functionality, weather, and a growing multiplayer implementation.

The project is not finished, and there will be bugs. Nevertheless, every implemented system brings it one step closer to the goal of recreating the original TDU2 experience in Unity3D.

**This is a work in progress. Enjoy exploring the project!**