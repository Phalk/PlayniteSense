# PlayniteSense

PlayniteSense is a Playnite extension that allows you to seamlessly use a DualSense controller with games that lack native support. It acts as a wrapper, converting your DualSense inputs into either Xbox 360 or DualShock 4 signals, effectively bypassing the need to run Steam's controller configuration.

## Features

*   **Steam-Free Emulation:** Use your DualSense controller without relying on Steam to manage input mapping.
*   **Per-Game Configuration:** Don't settle for a global setting. Easily toggle between Xbox 360 or DualShock 4 emulation for each specific game directly via the Playnite context menu.
*   **Native Playnite Integration:** Designed to feel like a native part of your library management workflow.

## Prerequisites

This extension requires specific drivers to handle the low-level communication between your hardware and the operating system. Please ensure the following are installed and updated:

*   **[ViGEmBus](https://github.com/ViGEm/ViGEmBus):** Required for virtual game controller emulation.
*   **[HidHide](https://github.com/ViGEm/HidHide):** Essential for hiding your original controller input, preventing "double input" issues in games.

*Note: These are standard drivers frequently used by controller emulation software. If you have used tools like DS4Windows in the past, you may already have them installed.*

## Installation

1.  Download the latest release from the [Releases page](https://github.com/your-username/PlayniteSense/releases).
2.  Open Playnite.
3.  Go to `Add-ons` -> `Install from file...` and select the `.pext` file you downloaded.
4.  Restart Playnite to ensure the extension initializes correctly.

## How to Use

1.  Right-click any game in your Playnite library.
2.  Navigate to the **PlayniteSense** submenu.
3.  Select your preferred emulation mode:
4.  Launch the game. The extension will automatically handle the mapping before the game starts.

## Troubleshooting

*   **Double Input:** If your game is registering two controllers, ensure that **HidHide** is correctly configured to hide your physical DualSense controller from the system.
*   **Controller Not Detected:** Verify that **ViGEmBus** is installed and that you have restarted your system if prompted during the driver installation.


## Why I created this (Motivation)

Many games on platforms like Epic and GOG do not support the DualSense controller natively (specially via Bluetooth). Previously, to make them work, it was necessary to add them to Steam to leverage the Steam API, and then import those shortcuts into Playnite (which is the reason why I created my other plugin, `SteamNonSteamImporter`).

However, this workflow created a significant bottleneck: it limited my ability to manage my complete library, as I could only apply these configurations to games that were already installed. With this addon, I can leverage Playnite's full library management capabilities without needing Steam as an intermediary, allowing me to use my DualSense with any game in my library without the usual headaches.
