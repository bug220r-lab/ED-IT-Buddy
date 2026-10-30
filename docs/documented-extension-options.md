# Documented MAGIX extension options

Research date: 2026-10-29. Target: Movie Edit Pro / Video deluxe 2021 Plus Steam Edition, Steam app 1363250. This investigation used downloaded documentation and primary websites. No editor, installer, plug-in, help viewer, or browser window was launched; no installed MAGIX file was changed.

## Exact product manual obtained

The complete [official Steam manual](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586) was downloaded and parsed locally: 361 pages, 60,572,861 bytes. It includes the 2021 features section and Steam-specific purchasing note. It covers multiple editions, so a documented capability is not proof that this particular installed build exposes it.

Local research copies:

- `.build/research/manual/movie-edit-pro-2021-steam.pdf`
- `.build/research/manual/movie-edit-pro-2021-steam.txt`
- PDF SHA-256: `2908E528FF0E681873DE13830875215446BA5EB1A0479F979EFAD8D7282CE824`

## Documented entry points

These are concise findings; page numbers refer to that manual.

| Pages | Finding |
| --- | --- |
| [155](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=155) | OFX: install → Effects → Extra effects → Scan → restart. VirtualDub, Adorage and VitaScene are also supported. |
| [157](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=157) | Plug-in manager’s Advanced dialog configures startup search directories, recursion and extension-only checking. |
| [135, 155](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=135) | Effect configurations can be saved, loaded and reused through My Presets. |
| [292, 308](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=308) | Titles support reusable templates; special effects can include grouped overlay objects. |
| [74, 167–168](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=74) | Plus/Premium allows external audio/picture-editor EXE paths. A compatible graphics editor can return overlay objects. |
| [158](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=158) | Externally drawn JPEG images can supply effect masks. |
| [186–187](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=186) | Plus/Premium supports VST audio effects, with configured directories and object/track/master effect racks. |
| [293, 324](https://cdn.cloudflare.steamstatic.com/steam/apps/1363250/manuals/Videodeluxe_EN.pdf?t=1599654586#page=293) | Window arrangements and menu keyboard shortcuts can be customized and saved. |

## Independent vendor evidence and practical constraints

Neat Video explicitly added Movie Edit Pro 2021 / Video deluxe 2021 support in version 5.4.0. This is historical, version-specific vendor evidence rather than an inference from a current compatibility list. Its Magix product line is distinct from its VEGAS and general OpenFX products. [Neat Video version history](https://www.neatvideo.com/features/version-history/nv5mg)

The vendor’s v5 instructions require that MAGIX has run at least once before installation, use its default installer settings, and locate the installed effect in MAGIX’s Effects area. They also describe applying the effect to a clip and opening its own interface. This establishes a practical effect-dialog integration, not an application-wide project API. No Neat Video package was downloaded or installed here. [Neat Video v5 guide for Magix](https://www.neatvideo.com/support/quick-start-guides/nv5/mg)

MAGIX’s own add-on support page distinguishes installers by manufacturer. For proDAD, installation selects a MAGIX host product; add-ons become available after restarting the host. The same page describes Vasco da Gama as a standalone application whose resulting video is imported into MAGIX. Consequently, “MAGIX add-on” does not by itself imply an embedded panel or shared timeline access. This is a current support page, not an exact 2021 Steam entitlement list. [MAGIX add-on installation instructions](https://www.magix.com/ie/support/technical-support/installing-and-activating-add-on-programs/)

Pixelan currently lists separate MAGIX and VEGAS/OFX targets and offers large preset browsers through its plug-in interface. This reinforces the feasibility of a substantial interface behind an effect, while establishing neither arbitrary docked panels nor compatibility of every binary with this Steam build. Its current installer guidance is not a recommendation to install anything. [Pixelan’s official download and host requirements](https://www.pixelan.com/download/demos.htm)

VirtualDub publishes a C/C++ plug-in SDK, including a newer interface covering 64-bit filters. Its basic filter contract operates on image buffers. That is a real development route worth distinguishing from OFX, but compatibility of specific SDK features in MAGIX still needs a host test; VirtualDub input-driver support must not be assumed to transfer to MAGIX. [VirtualDub Plugin SDK](https://www.virtualdub.org/filtersdk)

## What is not established

The full extracted manual contains no matches for `SDK`, `API`, or `script*`. Targeted searches of MAGIX’s official documentation did not reveal a public Movie Edit Pro 2021 application scripting SDK or general project/timeline API. This is a bounded research result, not proof that private interfaces do not exist. VEGAS scripting documentation does not answer this question.

The external-editor setting is an interesting launch/handoff avenue. The available documentation does not specify a public calling convention, supported document exchange protocol, or how a third-party application registers as an eligible overlay editor. Configuring Buddy as that executable would therefore be an experiment, not a documented integration. Likewise, saved window layouts do not demonstrate a third-party panel registration mechanism.

No documented file-format specification for generating MAGIX title/effect templates was found. A template made and exported by MAGIX is a stronger test fixture than a hand-written imitation. The plug-in manager’s configurable search path should also not be assumed to override the separate OFX discovery rules.

## Implications for ED-IT Buddy

The strongest immediate bridge is a companion that keeps the board separate and produces explicit handoff assets: annotated stills, scene notes, and optional exported masks or overlay images. Each card can record the asset it generated, the effect/template the user chose, and a manually confirmed application step. A project file observation should remain a separate timestamped fact.

A useful next offline proof is an export contract: a board drawing becomes a named image plus a small manifest tying it to its card. Do not automatically treat every board doodle as intended video artwork. The user should choose which drawing to export and where it belongs. Importing it into MAGIX remains a separate action.

For an eventual inside-editor experiment, the smallest honest milestone is a harmless x64 effect with a button that opens Buddy and persists one opaque card ID in the effect’s parameters. Test it on a disposable project when computer interaction is authorized again. This would prove effect-host communication, parameter persistence and lifecycle behavior; it would still not prove a dockable whiteboard, access to every track/object, or completion detection.

Separately, a future external-editor adapter could be investigated using a logging stub and disposable media to discover documented launch behavior. That should not replace an existing editor setting until its arguments, save/close expectations and rollback are understood.
