# TDU2 to Unity3D

**An attempt to bring Test Drive Unlimited 2 into Unity3D — and the foundation for something bigger.**

TDU2 to Unity3D was a personal project dedicated to recreating the world, gameplay systems, and overall experience of *Test Drive Unlimited 2* using the Unity3D engine.

The project aimed to bring as much of the original game as possible into a modern, flexible environment, including its maps, vehicles, interiors, racing activities, original interfaces, radio system, weather, dynamic time cycle, and online multiplayer functionality.

Over time, a significant amount of work went into understanding the original game's file formats, reverse-engineering its systems, and rebuilding its functionality from scratch.

However, the project eventually reached a point where the limitations of the Unity3D engine became increasingly difficult to work around.

---

## Project Status

**This project has been discontinued and is no longer actively developed.**

After spending a considerable amount of time working with Unity3D, I decided to move away from it and start developing my own game engine.

The main reason behind this decision was performance.

Despite implementing custom systems and continuously optimizing the project, Unity introduced limitations and overhead that made it increasingly difficult to achieve the performance, flexibility, and level of control I wanted.

Too much development time was spent working around engine limitations instead of improving the actual game and its systems.

Rather than continuing to fight against these limitations, I decided to take a different approach:

**Build my own engine, with the architecture, performance, and control I need.**

This is not the end of the TDU2 project. It is a change in direction.

The experience, research, reverse-engineered formats, and systems developed during the Unity project provide a foundation for the next stage of development.

## The Next Chapter – Project TDU2

The development of TDU2 is continuing on a new foundation: **my own custom game engine.**

Instead of adapting everything to the restrictions of an existing engine, I now have direct control over the core technology and can build systems specifically around the requirements of the project.

The new engine is being developed with a focus on:

- Vulkan-based rendering.
- Custom rendering and shader systems.
- Custom asset and resource loading.
- A dedicated audio engine and sound mixer.
- Custom physics and gameplay systems.
- BepuPhysics for collision detection and physics functionality.
- Greater control over memory management and performance.
- An architecture designed specifically for the requirements of TDU2.

The goal is to create a much more flexible foundation that allows the project to grow without constantly having to work around limitations imposed by an existing engine.

**Continue following the project here:**

[**Project TDU2 – GitHub Repository**](https://github.com/kuxii2016/Project-TDU2)

---

## What Was Achieved in the Unity Version?

Although development has stopped, the Unity project represents a considerable amount of work and experimentation.

Some of the implemented systems include:

### World and Environment
- Hawaii, Ibiza, and Casino map loading.
- Terrain and road rendering.
- Vegetation rendering.
- World object collision loading.
- Animated textures.
- Traffic spawning and movement.
- Dynamic day/night cycle.
- Weather system.

### Vehicles and Gameplay
- Vehicle loading from original game databases and files.
- Vehicle spawning and management.
- Vehicle dirt system.
- Partial garage and house interiors.
- Racing activities and driving schools.
- Selected original game features and interactions.

### UI and Audio
- Original Flash-based game interfaces.
- Sticker Shop functionality.
- Original TDU2 radio system.
- Kaluna Radio for custom music.
- Weather and time-related radio messages.

### Online Multiplayer
- Player synchronization.
- In-game chat.
- Vehicle position and rotation synchronization.
- Lights and wheel synchronization.
- Vehicle paint and sticker synchronization.
- Weather and time synchronization.
- Traffic and traffic light synchronization.
- Radio synchronization.

These features were implemented to varying degrees of completion. The Unity version was experimental and never reached the level of completeness originally intended.

---

## Screenshots

The following screenshots show selected stages of development and some of the functionality implemented in the Unity version.

<p align="center">
  <img src="./Screenshots/MP.PNG" width="49%" alt="Multiplayer"/>
  <img src="./Screenshots/MP2.PNG" width="49%" alt="Multiplayer"/>
</p>

<p align="center">
  <img src="./Screenshots/MP_INVITE.PNG" width="49%" alt="Multiplayer Invite"/>
  <img src="./Screenshots/MP_LIVING.PNG" width="49%" alt="Multiplayer Living"/>
</p>

<p align="center">
  <img src="./Screenshots/MP_GARAGE.PNG" width="49%" alt="Multiplayer Garage"/>
  <img src="./Screenshots/MP_GARAGESYNC.PNG" width="49%" alt="Garage Synchronization"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt.PNG" width="49%" alt="World Rendering"/>
  <img src="./Screenshots/Unbenannt1.PNG" width="49%" alt="World Rendering"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt2.PNG" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/Unbenannt3.PNG" width="49%" alt="Development Screenshot"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt4.PNG" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/Unbenannt5.PNG" width="49%" alt="Development Screenshot"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt6.PNG" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/Unbenannt7.PNG" width="49%" alt="Development Screenshot"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt8.PNG" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/bikes.PNG" width="49%" alt="Bikes"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt14.png" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/Unbenannt15.png" width="49%" alt="Development Screenshot"/>
</p>

<p align="center">
  <img src="./Screenshots/Unbenannt16.png" width="49%" alt="Development Screenshot"/>
  <img src="./Screenshots/Unbenannt17.png" width="49%" alt="Development Screenshot"/>
</p>

<p align="center">
  <img src="./Screenshots/bikedealer.PNG" width="49%" alt="Bike Dealer"/>
  <img src="./Screenshots/biketestdrive.PNG" width="49%" alt="Bike Test Drive"/>
</p>

---

## Requirements and Original Game Assets

The Unity project requires an original copy of *Test Drive Unlimited 2*.

No original game assets are included in this repository. Users must obtain and unpack their own copy of the game.

The original game files, including required resources such as `.cpr` and `.xmb` files, must be placed in the directory structure expected by the project.

Refer to the repository's existing configuration and setup instructions for further details.

---

## Disclaimer

TDU2 to Unity3D is an independent fan-made project and is not affiliated with, endorsed by, or associated with Eden Games or Atari.

*Test Drive Unlimited 2*, its trademarks, and original game assets remain the property of their respective rights holders.

This repository does not distribute original game assets.

---

## Final Words

What started as an experiment to bring *Test Drive Unlimited 2* into Unity3D eventually became something much bigger: an opportunity to understand the original game, explore its internal systems, and learn what it takes to rebuild an entire game experience.

Unity3D was an important part of that journey, but it also showed me what I wanted from an engine — and why building my own was the right next step.

**Sometimes the best solution isn't to keep working around the limitations, but to build something without them.**

The Unity version may be discontinued, but the journey of rebuilding TDU2 continues.

**Thank you to everyone who followed the Unity project!**

See you in the next chapter.

[**Project TDU2 – Continue the development**](https://github.com/kuxii2016/Project-TDU2)