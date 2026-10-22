# RageCoop Toolkit

## 🇬🇧 English · [🇷🇺 Читать по-русски →](README.ru.md)

**Play GTA V Legacy with friends, with a convenient shared-world menu.**

Work in progress · MIT · Game interface currently in Russian

**[Download v0.1.0-wip](https://github.com/vkupyansky/ragecoop-toolkit/releases/tag/v0.1.0-wip)** · [Source code](https://github.com/vkupyansky/ragecoop-toolkit)

An unofficial companion for [RageCoop](https://ragecoop.com/). Adds vehicle tools, a personal garage, teleports and shared world settings, alongside targeted fixes for the supported RageCoop build. The underlying multiplayer engine is RageCoop.

### What you can do

- Find and spawn GTA vehicles; keep favourites and save customised cars in your garage.
- Use ordinary Los Santos Customs and share your car's appearance with other players.
- Repair a car as its driver or passenger, put it back on its wheels, and use a held-key boost.
- Teleport to a waypoint or a friend, including a free passenger seat.
- Share a map marker, weather and time of day.
- Choose weapons, replenish ammo, and control health, armour and character abilities.
- Use keyboard and gamepad controls; type Russian and English in searches and garage names.

English game-menu translation and a language switch are planned.

### Release status

**Work-in-progress release: v0.1.0-wip.** Clean source builds and server startup checks passed. The public menu was checked in GTA on Mac: vehicle search/spawning, repair, boost, garage, waypoint teleports, weather and time. Gamepad menu opening and typing «з» also worked. Shared play has been tested with two players in the private setup. A fresh installation of the complete public client on Windows has not been independently confirmed.

### Screenshots

Real screenshots from a two-player session. The game menu is currently in Russian.

![Shared free roam with a friend](screenshots/01-shared-play.png)

<details>
<summary>Menu, vehicle catalogue, garage and friends</summary>

**Vehicles, teleports and shared settings**

![Toolkit main menu](screenshots/02-main-menu.png)

**GTA vehicle catalogue**

![Standard GTA vehicles in the catalogue](screenshots/10-vehicle-catalog.png)

**Saved cars, colours and tuning**

![Personal garage with a saved car selected](screenshots/09-garage.png)

**Teleport to a friend**

![Friend selected in the partner menu](screenshots/08-partners.png)

</details>

### Install

Supported game: **GTA V Legacy 1.0.3889** on Windows. Enhanced is not supported by this candidate. The addon ZIP includes the compatible RageCoop client and server; you do not need a separate RageCoop download.

**Joining a friend — Windows / VK Play**

SHVDN requires .NET Framework 4.8 (already included in Windows 10/11) and the [Microsoft Visual C++ v14 Redistributable, x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). Install the latter if it is missing.

1. Run Story Mode once, then close GTA. Disable BattlEye for Story Mode in the Rockstar Games Launcher; Steam/Epic users can use the official `-nobattleye` launch option. See [Rockstar's instructions](https://support.rockstargames.com/articles/1nenwhZlVrJY6CTFeSS2Fx/grand-theft-auto-online-battleye-faq).
2. Install [ScriptHookV 3889.0](https://www.dev-c.com/gtav/scripthookv/): copy `ScriptHookV.dll` and `dinput8.dll` from its `bin` folder beside `GTA5.exe`.
3. Download [SHVDN 3.7.0-nightly.188](https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases/tag/v3.7.0-nightly.188). Copy `ScriptHookVDotNet.asi`, `ScriptHookVDotNet2.dll` and `ScriptHookVDotNet3.dll` beside `GTA5.exe`. Keep an existing `ScriptHookVDotNet.ini`.
4. Extract `RageCoop-Toolkit-Addon.zip`. Copy the **contents** of `client` beside `GTA5.exe`, preserving its `scripts` folders. On VK Play, perform every step inside the virtual PC on its persistent game drive.
5. Fully start GTA again → Story Mode → **F9** → the host's address. Press **J** for the toolkit.

The host installs the server part below. RageCoop then downloads the menu automatically.

**Hosting for friends**

1. Install the [**.NET 10 Runtime, x64**](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), under **Run apps → .NET Runtime**; the tested version is 10.0.13.
2. Extract the ZIP and put its `server` folder somewhere writable. It is the complete server package; no GTA installation is needed on the host machine.
3. Open `start-toolkit.cmd` on Windows, or run `sh start-toolkit.sh` on Linux/macOS with x64 `dotnet` on PATH. The first launch creates `Settings.xml` and starts the server on UDP **4499**. It is not listed publicly; use it with people you trust, since every connected player can use the shared tools.
4. Give friends the address: on the host PC, `127.0.0.1:4499`; on the same network, the host's LAN address followed by `:4499`. From another network, forward UDP **4499** to the host and allow it through the firewall, then use the host's public address, or use a suitable private network. Stop the server with **Ctrl+C**.

To update an existing Toolkit server, stop it and replace the package files while keeping `Settings.xml` and player data. In `Resources/Server/CoopAdmin`, keep only `CoopAdmin.dll`. The included RageCoop **1.5.12.15** binaries are pinned in the release manifest: a different nightly with the same version number is not necessarily compatible.

After updating client files or the downloaded menu, fully restart GTA. F9 reconnect alone may reuse an older loaded assembly.

### Controls

| Action | Keyboard | Xbox |
| --- | --- | --- |
| Toolkit menu | J | Hold LB + RB, press ↓ |
| Repair | K | Hold LB + RB, press ↑ |
| Select / back | Enter / Esc | A / B |
| Navigate | Arrow keys | D-pad |
| Boost | Hold O | Not assigned |
| GTA pause / map | Esc | Menu / Start |

In a text field, character and editing keys belong to that field. Player preferences, favourites and garage entries are saved locally for the server.

### Limits and next steps

- Free roam and shared tools; full co-op story progression and synchronised cutscenes are not implemented.
- Radio station/on-off state is shared; exact song position and personal music files are not.
- Private testing covered two players, not an eight-player load test. No measured FPS increase or guarantee of zero crashes is claimed.
- Windows Esc and the remaining text-editing actions still need confirmation. A cold Win32 keyboard-layout check has failed and remains open, despite successful live input on Mac.
- Later: English menu and language switch, further independent stability reviews, then separate decisions about missions and other additions.

### Additional cars

Additional models are optional and installed separately by each player. They need matching files on all clients and a matching menu catalogue. Model downloads and their authors' permissions remain separate from this project's MIT licence.

### Source and contributions

See [engine changes](ENGINE_CHANGES.md) for confirmed issues in the tested engine, their fixes and evidence limits. This is not a claim that every upstream release has those defects. Fixes are applied by the addon; it does not replace RageCoop with a new engine.

For a source build, extract both source and addon ZIPs. Install the SDK pinned in `work/RageCoopAdmin/global.json` (**10.0.402**). Copy `References.example.props` to `References.props` there: point `RageCoopClientDir` to the addon's `client/scripts/RageCoop`, `RageCoopServerDir` to `server`, and `LemonUiDll` to `lib/net48/LemonUI.SHVDN3.dll` from [LemonUI.SHVDN3 2.2.0](https://www.nuget.org/packages/LemonUI.SHVDN3/2.2.0). Build from `work/RageCoopAdmin`:

```sh
dotnet restore Client/RageCoopAdmin.Client.csproj --locked-mode -p:PublicRelease=true -warnaserror
dotnet build Client/RageCoopAdmin.Client.csproj -c Release --no-restore -p:PublicRelease=true -warnaserror
```

Use the same locked restore/build commands for Server, Bootstrap and EarlyClient. From the source root, `bash tools/check.sh --public-release` runs the public builds and targeted checks on Windows/Git Bash or macOS/CrossOver with .NET Framework 4.8. It does not run the private full-audit suite or start GTA. Known limitations and failed checks must stay visible.

Our code is [MIT](LICENSE). RageCoop and other dependencies retain their own licences and credits in [THIRD_PARTY.txt](THIRD_PARTY.txt). ScriptHookV is downloaded from its author separately.
