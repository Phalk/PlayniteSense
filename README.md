# PlayniteSense

PlayniteSense is a Playnite extension that allows you to seamlessly use a DualSense controller with games that lack native support. It acts as a wrapper, converting your DualSense inputs into either Xbox 360 or DualShock 4 signals.

## Features

*   **Per-Game Configuration:** Don't settle for a global setting. Easily toggle between Xbox 360 or DualShock 4 emulation for each specific game directly via the Playnite context menu.
*   **Native Playnite Integration:** Designed to feel like a native part of your library management workflow.
*   **Driver checks:** PlayniteSense checks both required drivers at startup and before virtual controller emulation, and offers to open their official download pages when unavailable.
*   **Optional desktop controls:** Separate settings enable mouse and Windows shortcuts during games (including games without virtual controller emulation) and outside games while Playnite is running. The outside-games listener automatically resumes after a game ends and reconnects when a controller becomes available.

### Desktop controls

Hold Create/Share and Options for 0.6 seconds to toggle desktop mode. Repeat the combination to resume gameplay.

One short vibration confirms desktop mode is enabled. Two short vibrations confirm it is disabled. Feedback works with supported controllers over USB or Bluetooth when the input backend supports rumble.

| Input | Action |
| --- | --- |
| Swipe touchpad / right stick | Move cursor |
| Touchpad click / Cross | Left click |
| Circle | Right click |
| L1 + Options | Alt+Tab |
| Hold R1 + Options for 1 second | Alt+F4 |

Virtual gamepad input is neutral while desktop mode is active. Outside games and in games using the physical controller directly, applications may also receive its button presses. Bluetooth basic reports do not provide touch coordinates; use the right stick if touch input is unavailable. Windows may restrict injected input into elevated applications.

## Prerequisites

This extension requires specific drivers to handle the low-level communication between your hardware and the operating system. Please ensure the following are installed and updated:

*   **[ViGEmBus](https://github.com/ViGEm/ViGEmBus):** Required for virtual game controller emulation.
*   **[HidHide](https://github.com/ViGEm/HidHide):** Essential for hiding your original controller input, preventing "double input" issues in games.

*Note: These are standard drivers frequently used by controller emulation software. If you have used tools like DS4Windows in the past, you may already have them installed.*

## Installation

1.  Download the latest release from the [Releases page](https://github.com/Phalk/PlayniteSense/releases) and open the pext file.
2. Restart Playnite.

## How to Use

1.  Right-click any game in your Playnite library.
2.  Navigate to the **PlayniteSense** submenu.
3.  Select your preferred emulation mode:
4.  Launch the game. The extension will automatically handle the mapping before the game starts.

## Troubleshooting

*   **Double Input:** If your game is registering two controllers, ensure that **HidHide** is correctly installed and working.
*   **Controller Not Detected:** Verify that **ViGEmBus** is installed and that you have restarted your system if prompted during the driver installation.


## Why I created this (Motivation)

Many games on platforms like Epic and GOG do not support the DualSense controller natively (specially via Bluetooth). Previously, to make them work, it was necessary to add them to Steam to leverage the Steam API, and then import those shortcuts into Playnite (which is the reason why I created my other plugin, `SteamNonSteamImporter`).

However, this workflow created a significant bottleneck: it limited my ability to manage my complete library, as I could only apply these configurations to games that were already installed. With this addon, I can leverage Playnite's full library management capabilities without needing Steam as an intermediary, allowing me to use my DualSense with any game in my library without the usual headaches.
