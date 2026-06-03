# AutoUpdater
 The Auto Updater plugin automates the update process for your Counter-Strike 2 (CS2) server.
 > [!IMPORTANT]  
 > It is required for the server to have hibernation disabled: `sv_hibernate_when_empty` set to `false`.

# Features
 - Automatically checks the current game version of Counter-Strike 2 by querying Steam's API.
 - Notifies players about the upcoming server restart in chat.
 - Translations.

# Installation

 ### Requirements

  - [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master) (Dev Build)
  - [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (Version `369` or higher)

  Download the latest release of AutoUpdater from the [GitHub Release Page](https://github.com/M-archand/AutoUpdater-CSS/releases).

  Extract the contents of the archive into your `counterstrikesharp/plugins` folder.

# Configuration
 ```jsonc
{
  "ConfigVersion": 3,
  "UpdateCheckInterval": 180,              // Seconds between Steam update checks
  "ShutdownDelay": 120,                    // Seconds to wait before restart (delayed path)
  "ShutdownMessageInterval": 30,           // Seconds between repeated chat warnings during countdown
  "MinPlayersInstantShutdown": 0,          // Player count at or below this restarts instantly, above it uses a delay
  "ShutdownOnMapChangeIfPendingUpdate": true // Restart at map change if an update is pending
}
 ```