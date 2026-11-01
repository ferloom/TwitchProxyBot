# TwitchProxyBot

A joke simulator of a Twitch bot panel, built in the Windows 11 style.

> **This is not a working tool.** The app doesn't connect to Twitch or any other service, and it doesn't add bots, viewers or followers. All numbers and names are randomly generated on your machine, and the IP addresses come from ranges reserved for documentation (RFC 5737).

## Features

- Mica, Mica Alt or Acrylic backdrop and rounded window corners
- Light and dark themes, plus a mode that follows the system
- Windows 11-style controls
- Simulated campaign with progress, stats and a live chart
- Fictional event log and a searchable bot list
- Campaign history for the current run

## Requirements

- Windows 11 (the backdrop effects need it)
- .NET Framework 4.8

## Running

Download `TwitchProxyBot.exe` and run it. No installation is needed.

## Building

All you need is the C# compiler included with .NET Framework:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## Tech stack

C# and WPF on .NET Framework 4.8, with window effects through the DWM API.

## Disclaimer

This project was made for fun and as a UI exercise. It isn't affiliated with Twitch. Don't use it to mislead anyone, for example by pretending to sell real bots.
