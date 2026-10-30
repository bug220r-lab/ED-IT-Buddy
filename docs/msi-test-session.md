# MSI test session, 2026-10-29

## SSH now works

A dedicated Ed25519 key was generated on THIS-MACHINE at `C:\Users\Jude\.ssh\edit_buddy_msi_ed25519`. Its private material stays outside this workspace and OneDrive. Public fingerprint: `SHA256:O6Yr3i/MENIjsGx6iKotW1EWrhnkXyR+yNm1nWyvm/Q`.

The user authenticated the installation command interactively. MSI's effective OpenSSH configuration selected `C:\ProgramData\ssh\administrators_authorized_keys`. The dedicated entry restricts source to `192.168.247.2` and disables agent, port and X11 forwarding. This is Windows' shared administrators key file; it is not a per-user filesystem sandbox. Existing keys and SSH service configuration were preserved. Batch-mode, key-only verification returned `MSI` and `msi\jlewi`.

Use explicit `-i`, `IdentitiesOnly=yes`, `BatchMode=yes`, and `StrictHostKeyChecking=yes`. No general SSH config was changed. Revocation would remove only the entry containing public-key blob `AAAAC3NzaC1lZDI1NTE5AAAAIKw+n6WiSItqFL0pOxIVW0BAxHfzDJ0T0vN9kdbBnpXJ`, preserving all other entries. No revocation was performed.

## Environment and copy

MSI runs Windows 11 Home, build 26300, with 15.8 GiB RAM and about 111 GiB initially free on C:. Windows Sandbox is not supported on Home. The user declined an evaluation VM and asked for a direct test on MSI.

Installed MAGIX is executable version 20.0.1.80. Its RCC hash matches THIS-MACHINE's researched build. A complete copy was prepared at:

`C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Test\MAGIX-20261029-29978855`

It contains 21,954 files totaling 9,990,721,472 bytes. File count and total size were compared; EXE and RCC hashes were checked individually. This was not a full hash comparison of every file. No user project was copied.

The test copy received the previously verified RCC that changes only the Object Editor OK label to `OK *`. Copied `install.cfg` paths were redirected into the copy, with a new RegKey and fresh data directories. Per-process environment variables pointed at a new test profile. These settings were experimental, not proven isolation.

## Launch result: Steam redirected to the installed editor

The test launcher started copied `Videodeluxe.exe` as PID 27456 at `2026-10-30T00:05:38.1571632Z` (8:05:38 PM Eastern, October 29). It passed no project argument.

That process exited and Steam (PID 14328) started the **original installed** `Videodeluxe.exe`, PID 4696, at approximately 00:05:41Z. The observed path was under `C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition`, not the test-copy directory. Therefore the patched interface was not validated.

After checking PID, original executable path and start time against the test, the redirected process was stopped. A subsequent check confirmed zero `Videodeluxe` processes. The one-time scheduled launcher task was removed.

## Actual effects and verification limits

- Original EXE, `install.cfg`, and `qml\resource.rcc` hashes matched their pre-launch values.
- The normal profile's `Videodeluxe.ini` hash **changed**. A pre-launch hash was captured, but not a complete pre-launch copy; no speculative restoration was attempted.
- The normal MAGIX Documents folder's `Slideshow music.lnk` was written during startup. No project/autosave write was found in that folder's checked top-level files.
- The known original `redacted sample test .MVP` retained a modification timestamp from before this test. This is a timestamp check, not a before/after content hash for every project.
- MAGIX's normal startup log also received writes. This direct launch did not preserve every user-settings file unchanged, despite the intended copy-only test.
- Later inspection also found the normal shared `C:\ProgramData\MAGIX\Videodeluxe27_plus_steam\installation.ini` and `Videodeluxe.ini` with write timestamps at 00:05:46Z and 00:05:45Z respectively, during that first redirected launch. No pre-first-test copies of these files were captured.

The post-redirect INI was preserved on MSI as `ED-IT-Buddy-Test\settings-after-redirect.ini`. Pre-launch hashes, copy receipt and launch receipt are also in that directory. The prepared test copy and dedicated SSH key remain; no test process remains running.

## Next investigation

Follow-up static analysis is recorded in [Steam launch research](steam-launch-research.md). The bridge directly calls SteamAPI_Init, and the main EXE enters through a separate `.bind` section. The ordinary restart-API text-file exception is not a verified fix for this wrapper.

Resolve Steam's executable redirection before another launch. Valve documents development-time behavior for `SteamAPI_RestartAppIfNecessary` and `steam_appid.txt`, but this exact MAGIX call path and its supported use have not yet been verified. This is a research lead, not a tested remedy or license bypass. Legitimate Steam ownership/activation must remain in place.

Do not rerun `launch-msi-magix-test.ps1` unchanged: it intentionally refuses repeated preparation. Do not present the copy's profile redirection as validated isolation. Any retry needs its own fresh verification, and the actual UI label remains untested.

Reference: [Valve Steamworks API overview](https://partner.steamgames.com/doc/sdk/api).

## Ethernet repair and verified settings backup

MSI later responded to ping while TCP port 22 timed out. The dedicated MSI diagnostic chat found that Ethernet interface 6 had reverted to Public; the existing firewall policy permits this connection only on Private. With the user's explicit approval, the chat used an inspected existing elevated task to restore only that adapter's network category to Private. The task validated the host, account, adapter GUID, IP, route state, peer IP, and peer MAC before making that change. It did not change IP addresses, routes, firewall rules, services, or SSH authentication. A separate key-only SSH check from THIS-MACHINE then returned `MSI`, `msi\jlewi`, and Ethernet `Private`.

The backup procedure completed on MSI at:

`C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Test\Backup-20261030T002828Z-3b85cf80`

Its `manifest.json` records 84 files totaling 86,942,349 bytes. Every backup file's SHA-256 matched its source. The scope is the normal MAGIX roaming profile, selected top-level configuration/shortcut files and `Autosave.MV_` in its Documents folder, and the known original project. `HKCU\Software\MAGIX` was also exported as `MAGIX-HKCU.reg`. MAGIX was closed during backup and verification. This preserves the state **after the first redirected launch**, not the lost pre-first-test INI state.

## Launch-guard validation

A disposable copy of Windows `whoami.exe` was tested on both machines. Holding its file open for reading with `FileShare.None` prevented a normal process launch with a sharing-violation error. Releasing the handle allowed the same helper to run with exit code 0. MSI's receipt is under `GuardProbe-8691dfabaa43405b8278b1aeae353e84\result.json` in the test directory. No MAGIX process was launched by these guard checks.

This verifies a guard against a fresh ordinary launch of the specific locked executable. It is not a sandbox: locks on existing files do not prevent new files or registry writes, and a controller crash would release its handles. A proposed bounded retry must acquire all guards before starting, retain the installed EXE guard beyond an early copied-process exit because Steam launch requests are asynchronous, and close owned test processes before releasing guards.

## Guarded retry: copied executable starts, then error -59

The reviewed controller `.build/research/launch-msi-magix-guarded.ps1` compiled under Windows PowerShell 5.1 on both machines. A disposable windowless helper verified owned-job termination and kill-on-close cleanup. That probe caught an initial module-enumeration race; the final launcher verifies the image path and creation time using retained native process handles before resuming the process.

MSI run directory:

`C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Test\GuardedRun-20261030T004253Z-54ab99c7`

The launcher acquired 87 file guards (84 backup sources plus the original EXE, install.cfg, and RCC). It temporarily placed `steam_appid.txt` containing `1363250` in the copy and launched without project arguments. PID 27844 was created at `2026-10-30T00:43:13.9023466Z`; its verified image path was the **copied** `Videodeluxe.exe`. Across 119 samples over 119.6 seconds, the title progressed from blank to `MAGIX Splash Screen` and then `MAGIX Movie Edit Pro`. Qt Core, QML, Quick, and Widgets modules loaded from the copied directory. No second installed MAGIX process was observed.

The user's photograph confirmed a startup dialog reporting **error -59**, stating that the program could not start and the installation folder may be damaged. The normal editor and altered `OK *` label were not reached. The test profile's log only records startup, GPU initialization, and shutdown; it does not name the failing dependency. Therefore the app-ID hint got this guarded attempt past the previous immediate redirect, but it did not produce a usable copied editor or prove the UI patch.

Cleanup completed at `2026-10-30T00:45:17.8322757Z`:

- All 87 final SHA-256 checks matched; controller errors: zero.
- The before/after `HKCU\Software\MAGIX` exports matched.
- The monitored normal roaming-profile and MAGIX Documents trees had no new files or file metadata changes.
- The owned MAGIX job was stopped before locks were released. No `Videodeluxe` process remained.
- The temporary app-ID file and one-time scheduled launcher were removed.

Local receipts are in `.build/research/msi-guarded-run-54ab99c7/`. Controller status `Completed` means the observation and cleanup completed, **not that MAGIX startup succeeded**.

### Confirmed shared-data isolation gap

The application honored the redirected roaming profile but created a new test-specific directory at the real system path:

`C:\ProgramData\MAGIX\Videodeluxe27_plus_steam_EDITBuddyTest29978855`

Its creation time was `2026-10-30T00:43:14.4884077Z`, during the owned test. This proves that changing the process's `PROGRAMDATA` environment variable did not redirect all shared-data access. The earlier monitored-tree result does not cover this new directory. It contained three files totaling 949 bytes. These were copied into the run's `generated-system-ProgramData` directory, every copy hash was verified, and only the exact test-created system directory was removed after validating its name, parent, creation time, size bounds, and lack of reparse points. `generated-folder-cleanup.json` records this cleanup. The normal shared profile was not removed.

The original shared `installation.ini` and `Videodeluxe.ini` still had the earlier 00:05Z write timestamps, not this retry's timestamps. Their hashes were captured afterward, but they were not among the 87 guarded files and have no pre-retry hash baseline. Do not describe the retry as a proof that all possible machine state was untouched.

The fresh test metadata and altered RegKey are plausible causes of the startup failure, but the exact cause remains unconfirmed. Missing installation state, a blocked access caused by the guard, or another copied-configuration dependency have not been distinguished. No registration values, license checks, or installed binaries were patched. Further runtime work must first account for the actual shared-data path and include an unmodified-resource baseline; do not run an installer repair against the original installation on the basis of this copied-test error.
