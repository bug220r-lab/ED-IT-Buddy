# MAGIX embedded whiteboard: bounded feasibility research

Inspected 2026-10-29. The initial investigation covered installed configuration text, printable binary strings, and PE import/export metadata. Its evidence is retained below, with conclusions updated after focused static instruction decoding and Qt resource inspection. No MAGIX process was launched, controlled, attached to, or modified by these investigations. No installed DLL was loaded or executed.

## Finding

This exact installation has an OpenFX effects host and an internal mechanism for displaying ordinary plug-in controls inside the Effects area. Later inspection found that the wrapper's host initializer explicitly sets both `OfxParamHostPropSupportsCustomInteract` and `OfxImageEffectPropSupportsOverlays` to **0**. A standard OFX drawing canvas or preview overlay should therefore **not be the recommended whiteboard route** for this build. See the [initializer and callback evidence](ofx-host-research.md).

An effect with ordinary parameters and a button opening Buddy remains a possible experiment. Separately, decoded private QML resources expose concrete host bindings for effect controls, preview state and selected-object metadata; the executable references several of those pages. This is a stronger private-UI research lead, but no supported resource override or third-party panel registration route has been established. See [Qt interface research](qt-interface-research.md). Neither path currently proves an independently dockable board or project-wide timeline API.

## Installed evidence

All paths below are relative to:

`C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition`

| File | Observed evidence | What it establishes / does not establish |
| --- | --- | --- |
| `ImageProcessingPlugins\OFX\Cookie Cutter.vsx` | `Interface=IMXVFXPlugin`, `PluginType=SingleFx`, `Creation=ComDll`, `CompatibleVersion=3`, `UseParameterReflection=1`, `UseQtReflection=1`. Both DLL entries name `MxVfxOfxWrapper_x64.dll`; parameters identify bundled `Vfx1.ofx` and `com.magixsoftware:cookiecutter`. | The installed product bridges OFX into MAGIX's internal effect interface and creates parameter controls. It does not describe a general workspace extension interface. |
| `ImageProcessingPlugins\MxVfxOfxWrapper_x64.dll` | PE named exports: `DllCanUnloadNow`, `DllGetClassObject`, `MXSetHost`. Printable strings include `IMXVFXPlugin`, `IMXVFXEngine2`, `IMXVFXParameterReflection`, `IMXClientServerExchange2`, `OfxGetPlugin`, `OfxGetNumberOfPlugins`. | A real internal COM-style adapter and OFX loading machinery exist. Interface signatures, lifetime rules, and compatibility guarantees were not recovered. Export names alone are insufficient to call these interfaces safely. |
| Same wrapper DLL | Initial string inventory included custom-interaction, overlay and timeline-suite identifiers. Later decoding found explicit zero initialization for custom parameter interaction and overlays, plus real version-1 timeline-suite dispatch. | String presence alone was inconclusive. The later initializer evidence argues against assuming standard OFX drawing support; the live capability values and downstream timeline behavior remain untested. [Details](ofx-host-research.md). |
| `ImageProcessingPlugins\OFX\Plugins\ofx360Rotation.ofx.bundle\Contents\Win64\ofx360Rotation.ofx` | PE named exports are `OfxGetNumberOfPlugins` and `OfxGetPlugin`. | Confirms a bundled actual OFX binary in this exact edition, independent of manuals for other editions. |
| `ImageProcessingPlugins\OFX\Plugins\ofx360Rotation.ofx.bundle\Contents\Resources\gui.xml` | A `<surface>` describes three angle sliders and a stereoscopy combo box. | There is installed data-driven UI for effects parameters. This does not show arbitrary HTML, a freeform scene canvas, or top-level panel registration. |
| `GUI\Explorer\CExplorerV3MainWin_Tab3_Skin01.ini`, around lines 341–370 | Child window placeholders `IDC_DIALOG_SM_VIDEO_PLUGIN_DUMMY`, `IDC_DIALOG_SM_VIDEO_PLUGIN_REMDLG_DUMMY`, and `IDC_DIALOG_SM_VIDEO_PLUGIN_REMDLGQT_DUMMY`, each with `WS_CHILD`. | The Effects area has slots for plug-in UI, including a Qt-related slot. These are existing internal placeholders, not a documented third-party panel registration contract. |
| `GUI\Effects\ParamRefl\ParameterReflectionDummy.ini` | Dialog name `IDD_REMOTE_DLG_EFFECTDUMMY_EMBEDDED`; an alignment helper is described as the starting point for controls reflected from the plug-in DLL. | Reinforces that in-application effect controls are supported internally. |
| `layout\_default.pmly` | `LAYOUT_OPT_DOCKING` and an explicit `allowedWindows` list containing MAINFRAME, VIDEO, EXPLORER, PROJECT_FREE, and MDICLIENT. | Existing MAGIX panes dock. The inspected layout does not provide a registration slot for a new external pane. Editing it cannot be assumed to create a new pane. |
| `Videodeluxe.exe` | Imports include Qt widgets, QML, Quick, WebEngineWidgets, and MFC. Its named exports inspected here are QWinWidget integration and Boost serialization symbols. | MAGIX has mixed native UI technology. These dependencies/exports are not evidence of a supported scripting or web-panel extension API. |

No header files, import libraries, or debug symbol files were returned by the bounded installed-file search for `*.h`, `*.lib`, and `*.pdb`. A `RegModule_x64\ScriptRunner_x64.dll` and Qt Script runtime DLLs exist, but no project scripting SDK was established from them.

Later Qt inspection decoded `qml/resource.rcc` without executing it. The package contains shared-product and development-looking assets, so presence alone does not prove use. Direct executable references to the preview, object editor and EasyFx pages, together with resource/context registration imports, strengthen those specific links. They still do not establish live reachability or a public interface. [Private binding inventory and loading limits](qt-interface-research.md).

## Distinguishing the possible integration levels

1. **Effect-bound ordinary controls inside the Effects area:** plausible and worth testing. Use conventional parameters and, potentially, a button opening Buddy. The explicit zero capability initialization makes a standard OFX custom canvas a poor assumption. Visibility/lifecycle may depend on the selected object and effect instance; even an identity effect adds a project dependency.
2. **Independent dockable whiteboard pane:** not established. The files demonstrate MAGIX's internal docking and plug-in UI, but no public registration API or supported pane lifecycle was found.
3. **A window visually attached to MAGIX from another process:** technically a different experiment, using native window integration rather than a MAGIX extension API. It would not grant project access. It has not been attempted and should not be presented as equivalent to a native dockable pane.
4. **Access to the whole project:** not established by any evidence above. OFX effect inputs and parameters are not a complete list of project tracks, clips, cuts, names, or scene structure.
5. **Private QML interface modification:** a separate research path. Selected-object and preview bindings are identifiable, but resource loading, native context creation and replacement behavior are not yet verified. This is not a supported extension mechanism merely because the packaged QML is readable.

The OpenFX timeline suite is especially easy to misread: its documented operations are current time, move to a time, and time bounds associated with an effect instance. It does not provide a timeline edit graph or enumerate project clips. Static decoding now establishes version-1 dispatch to three non-null callbacks, which forward to virtual methods on an effect-instance subobject. Their concrete downstream behavior and useful live results remain unverified. [Dispatch and callback table](ofx-host-research.md).

## A concrete next experiment

When application operation is authorized again, a bounded OFX experiment can use a minimal x64 identity effect built outside the installed application directory which:

- Passes its source frames through unchanged.
- Describes ordinary text/button parameters, without requiring a custom canvas or overlay.
- Records live host capability values and whether timeline-suite version 1 is returned.
- Queries time/bounds read-only while a person moves the playhead to known positions; it does not seek automatically or assume a non-null callback provides correct results.
- Keeps any board state local to the effect's own string parameter or a separately chosen test file; it does not assume access to the project path.

Use a disposable project through MAGIX's normal plug-in loading flow. Success criteria are ordinary controls, selection/lifecycle behavior, parameter persistence through save/reopen, verified time/bounds observations and clean removal. A button that opens Buddy would prove a launch bridge, not an embedded freeform canvas or independent workspace pane. No such experiment has been performed here.

If live capability values confirm zero, stop the standard OFX canvas branch. The separate [private-QML proposal](qt-interface-research.md) starts with one harmless label in a copied resource, after establishing an actual loading path; it does not begin by adding a board or changing project state. The [documented extension options](documented-extension-options.md) also cover reusable templates and external-editor handoffs.

## Primary specification references

- [OpenFX architecture and image-effect scope](https://openfx.readthedocs.io/en/latest/Reference/ofxStructure.html)
- [OFX host custom-interaction capability and parameter interaction properties](https://openfx.readthedocs.io/en/main/Reference/ofxPropertiesReferenceGenerated.html)
- [OfxTimeLineSuiteV1: current time, seek, and bounds](https://openfx.readthedocs.io/en/main/Reference/suites/ofxTimeLineSuiteReference.html)
- [Official Steam product listing for app 1363250](https://store.steampowered.com/app/1363250/)

The specification references explain API meaning. The installed observations and later initializer analysis provide edition-specific static evidence; only future runtime tests can establish live behavior. No claim of a verified public MAGIX SDK is made.
