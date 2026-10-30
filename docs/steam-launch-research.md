# MAGIX Steam launch: static findings

Inspected 2026-10-29 on THIS-MACHINE. Scope: read executable files as bytes, parse PE metadata, and disassemble bounded code. No MAGIX executable or DLL was loaded or run, and no installed file was changed.

## Conclusion

The inspected MAGIX Steam bridge does **not** import `SteamAPI_RestartAppIfNecessary`; its exported `getConnector` calls `SteamAPI_Init` directly. The main executable also has no ordinary Steam API imports or delay imports. Its entrypoint is in a separate `.bind` section, consistent with a wrapper running before the ordinary MAGIX program. This is a credible explanation for the observed MSI launch redirect, but static inspection here does not conclusively identify or trace the wrapper's restart implementation.

Consequently, `steam_appid.txt` is a documented Steamworks development mechanism, but **is not established as a fix for this MAGIX copy**. Do not claim it guarantees a launch without touching the registered Steam installation or normal user settings.

Runtime follow-up: one guarded MSI retry with that file did reach startup in the copied executable, instead of the earlier immediate Steam redirection. It then displayed MAGIX error -59 and never reached the editor. This supports the hint as a useful launch experiment for this build, not as a complete startup or isolation solution. See [the bounded test and cleanup record](msi-test-session.md#guarded-retry-copied-executable-starts-then-error--59).

## Evidence

Files are from `C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `Videodeluxe.exe` (20.0.1.80) | 27,431,504 | `3b193e4fbfd498636e71f625e5a7736eaeaf029c30f2a2e70382633c1558f989` |
| `MxSteamConnector_rel_u_dynCRT_x64_vc12.dll` | 20,480 | `7362b67df64bff8a28c670ff53b5ea434c34feeb581740cf3ae271ff407ec1f8` |
| `steam_api64.dll` | 249,120 | `2197363bfcf05fbdee84afd7dec6d58721587cb3dbf57c0f02b38e82c5cb5d41` |

### Bridge DLL

- Exports `getConnector` at RVA `0x1000`.
- At RVA `0x106C`, the instruction is `call qword ptr [0x180004138]`. That import-table slot is `steam_api64.dll!SteamAPI_Init`. The returned Boolean is stored at object offset `0x38` by the next instruction.
- Steam imports are `SteamAPI_Init`, `SteamAPI_UnregisterCallback`, `SteamAPI_RunCallbacks`, `SteamAPI_Shutdown`, `SteamAPI_GetHSteamUser`, `SteamAPI_RegisterCallback`, `SteamInternal_CreateInterface`, `SteamAPI_GetHSteamPipe`, and `SteamInternal_ContextInit`.
- No import of `SteamAPI_RestartAppIfNecessary`, no delay-import directory, and no direct `GetProcAddress` or `LoadLibrary*` imports.

### Main executable and Steam API library

- `Videodeluxe.exe` has no Steam imports in its ordinary import table; its delay-import directory is zero. General `LoadLibrary*` and `GetProcAddress` imports exist, so an import-table inspection cannot exclude dynamically resolved APIs.
- Main entrypoint RVA: `0x251E310`. `.bind` begins at RVA `0x251E000`, raw offset `0x19ED200`, size `0x38C98`. The first 48 entrypoint bytes save the instruction location and registers. That startup stub lies outside `.text`.
- No readable Steam-related strings were found in `.bind`. No attempt was made to decode protected content, remove a wrapper, or alter licensing checks.
- `steam_api64.dll` exports `SteamAPI_RestartAppIfNecessary` at RVA `0x8990` (ordinal 817). This proves availability, **not that MAGIX calls it**.
- A filename-only binary-string scan of installed `*.exe` and `*.dll` files for `SteamAPI_RestartAppIfNecessary`, `steam_appid.txt`, `steam://rungameid`, and `SteamAppId` matched only the main executable and Steam API DLL. The main executable's extracted Steam strings included `MxSteamConnector::SteamAppId`, rather than a readable restart symbol. Encoded strings and computed/dynamic calls remain outside this check.

## Official documentation and its limits

Valve documents that `SteamAPI_RestartAppIfNecessary` can relaunch the installed library version rather than the executable that invoked it. A development `steam_appid.txt` makes **that API** return false. The same page says the Steam DRM wrapper performs the launch-through-Steam check internally, making the API call unnecessary. [Steamworks API Overview](https://partner.steamgames.com/doc/sdk/api#SteamAPI_RestartAppIfNecessary)

Valve documents `steam_appid.txt` as an App ID hint during development. Successful API initialization still requires a running Steam client, the appropriate user context, and a license on the active account. [Steamworks initialization](https://partner.steamgames.com/doc/sdk/api#SteamAPI_Init)

Valve separately describes wrapping a primary executable and the wrapper's integration with other DRM. It does not establish that the development text-file exception controls this MAGIX binary's wrapper. [Steam DRM](https://partner.steamgames.com/doc/features/drm)

## Research artifacts and next-step implications

Machine-readable evidence: `.build/research/steam-static-imports.json`; Steam string inventory: `.build/research/steam-exe-inventory.json`; bounded bridge disassembly: `.build/research/steam-connector-disassembly.txt`.

The existing MSI runtime record is in `docs/msi-test-session.md`. Combining it with this static evidence supports treating the redirect as unresolved. Any authorized retry needs to retain ownership/licensing checks and be described as an experiment. A process guard cannot guarantee that an incorrectly started installed process writes nothing before being stopped. Backups are useful recovery measures, not proof of isolation.

The resource-label change can be verified structurally without relaunching MAGIX. A guaranteed isolated executable test still requires an independently isolated environment or a vendor-supported way to select the copied installation and its user state.
