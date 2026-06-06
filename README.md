# Twitch Studio Native Discord Overlay

Twitch Studio Native Discord Overlay is a Windows desktop app for showing Discord voice activity in a transparent browser overlay. It connects to the Discord desktop client, tracks who is speaking, muted, deafened, or disconnected, and serves per-user overlay pages for streaming software.

## Features

- Discord voice-channel state overlay
- Transparent browser-source overlay pages
- Per-user images for idle, speaking, muted, deafened, and disconnected states
- Multi-frame image animations
- Overlay import and export as zip archives
- Local microphone speaking detection
- Optional voice commands using the bundled Vosk speech model
- Hotkeys for local microphone mute and voice-command actions
- English and Russian UI localization

## Requirements

- Windows
- Discord desktop client
- .NET 8 runtime or SDK if running from source

## Run From Source

```powershell
dotnet restore
dotnet run --project TwitchStudioNative.csproj
```

To create a portable release archive:

```powershell
.\scripts\build-installer.ps1
```

The portable build is written to:

```text
release\TwitchStudioNative-win-x64.zip
```

If Inno Setup is installed, the same script also builds the installer.

## Basic Setup

1. Start Discord desktop.
2. Run the app.
3. Allow the Discord RPC authorization prompt if Discord asks for it.
4. Join a Discord voice channel.
5. Select a user in the app.
6. Import images for the user's overlay states.
7. Add the user's overlay URL to your streaming software as a browser source.

## Browser Source URL

The default overlay server address is:

```text
http://localhost:3847
```

Use this URL format for a specific Discord user:

```text
http://localhost:3847/overlay/user/{discordUserId}
```

Example:

```text
http://localhost:3847/overlay/user/123456789012345678
```

The overlay page has a transparent background and reconnects automatically if the app restarts.

## Overlay Images

Each user can have images or animations for these states:

- `idle`
- `speaking`
- `muted`
- `deafened`
- `disconnected`

Supported asset types:

- PNG
- APNG
- JPEG
- GIF
- WebP
- SVG

Multiple imported frames can be used as an animation. Frame duration can be adjusted in the app.

## Voice Commands

Voice commands can trigger custom overlay animations for the authenticated Discord user.

The app includes a bundled Russian Vosk model:

```text
Vosk\vosk-model-small-ru-0.22
```

You can configure:

- microphone device
- Vosk model path
- grammar mode
- command phrase
- command action name
- optional hotkey
- animation frames for the command

## Local Microphone Detection

Local microphone detection can be used to show speaking state even when Discord has the local user muted. Detection modes:

- RMS threshold detection
- Neural VAD using the bundled Silero ONNX model

You can configure microphone device, speaking threshold, silence threshold, attack time, and release time in settings.

## Data Location

User configuration and imported assets are stored in:

```text
%APPDATA%\TwitchStudioNative
```

Important files and folders:

- `config.json`: app settings
- `assets\`: imported overlay assets

## Troubleshooting

If Discord does not connect:

- Make sure the Discord desktop client is running.
- Join a voice channel.
- Use the reconnect button in the app.
- Check that Discord RPC authorization was accepted.

If the browser overlay is blank:

- Confirm the app is running.
- Open `http://localhost:3847/api/voice` in a browser.
- Check that the user ID in the overlay URL is correct.
- Import at least one image for the selected user.

If microphone detection does not work:

- Select the correct microphone device.
- Check Windows microphone permissions.
- Increase or decrease the speaking and silence thresholds.
- Try RMS mode if neural VAD is unavailable.
