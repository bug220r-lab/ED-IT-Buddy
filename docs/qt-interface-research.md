# Private Qt interface research

Read-only investigation, 2026-10-29. Target executable reports version **20.0.1.80**; its bundled Qt Core reports **5.7.0.0**. No QML, Qt executable, MAGIX executable, or plug-in was loaded or executed. Installed files were unchanged.

Evidence: `.build/research/qt-resource-inventory.json`, produced by `tools/inspect_qt_resource.py`. It describes 2,476 resource entries, including 465 QML source files, from the installed `qml/resource.rcc` (5,528,612 bytes; SHA-256 `36c61ceb9901f9b5cdd3d8a66888f36bdbdd7537f8a7ef87d8157dc868a03e8a`). Resource paths below are entries inside this inventory, not loose installed files. Line numbers refer to each entry’s decoded text.

## What the packaged UI actually exposes

| Resource beneath `/_resource/` | Concrete evidence | Meaning for Buddy |
| --- | --- | --- |
| `Dialogs/EasyFx/ParameterReflection/ParameterReflection.qml` | Lines 13, 85–98 contain a placeholder page title, an applied-state property, `applyButton(bool)` signal and `apply(newValue)` function. | Small private effect-page contract; it does not enumerate project objects or provide a general reflection API. Its placeholder presentation makes it a weak mutation target. |
| `Dialogs/EasyFx/EffectPage.qml` and `EffectPageHeader.qml` | Content/header replacement functions; header controls receive `vmEffectList`, `vmEffectName`, `vmEffectReset`, `vmEffectMenu`. | MAGIX supplies view models and decides which effect UI is inserted. Adding a QML file alone does not register a new native page. |
| `VxControls/Controls2/VxButtonFx.qml` | Lines 24–31 bind availability/label to `viewModel.props`; line 47 calls `viewModel.execute()`. | Existing custom controls dispatch into host-provided objects. No standalone automation endpoint follows from these bindings. |
| `Dialogs/EasyFx/Distortion/Distortion.qml` | Lines 46–68 pair numeric `objectName` identifiers with `qmlAdapter.currentlySelectedEffect`. | Private parameter IDs and selection-dependent wiring must be preserved; a generic replacement control cannot safely guess their meaning. |
| `Dialogs/ObjectEditorDialog/Placement/CommonProperties.qml` | Names, object length, source filename/size/length/date, software/copyright and comment; object name/comment use explicit write-back bindings. | A possible selected-object metadata surface, but not a list of the entire timeline. Names are not proven persistent object IDs. |
| `Dialogs/ObjectEditorDialog/Placement/ControlsBottom.qml` | Line 41 invokes `objectEditorViewModel.submitValues()` before the dialog closes. Cancel closes without that explicit call. | Reading and editing are distinct operations. Actual transaction/undo behavior still belongs to the native implementation. |
| `Dialogs/ObjectEditorDialog/Placement/MetaData.qml` | A native `metaListViewModel` supplies `metaKey` / `metaValue` roles. | Potential metadata display; schema, availability and object scope are not established. |
| `Mainscreen/PreviewMonitor/MxControls/VxControl_MiniTimeline.qml` | Reads `hostConnect_Preview.maxMovieFrames`, `frameRate`, `markerPosPly`, `markerPosSrt`, `markerPosEnd`, `sampleRate`, range/visibility flags. | Clear private access to preview state. No read-only external connection has been demonstrated. |

The preview mini-timeline converts marker positions as seconds for display (lines 443–497), then multiplies seconds by `sampleRate` before emitting `sigMakerMoved` (lines 648–698). Its parent declares that signal with marker number, numeric position and event code. This is a particularly important unit boundary: outgoing values must not be assumed to be video-frame counts. No calls were made.

`MxPlacement/Area1_Head.qml` also reads `movieName` and timecode arrays. `PreviewMonitor.qml` excludes its QML preview-image component; its comment identifies that area as owned by the native host. Therefore a board in a Qt controls panel and ink drawn over the actual playback image are separate integration problems.

## Bundled versus used

The resource includes multiple product themes/configurations, dummy host windows, project files and development-looking pages. `PreviewMonitor/_Configuration/Config.qml` switches by `VxGlobalConfig.productState`, including Video Pro X and Video deluxe variants. Presence in the resource is insufficient to claim a feature is active in this Steam edition.

However, static strings in **this installed `Videodeluxe.exe`** strengthen several links:

- `hostConnect_Preview`: ASCII file offset `0x1138390`.
- `commonPropertiesViewModel`: UTF-16LE file offset `0x10d5ae8`.
- `objectEditorViewModel`: ASCII file offset `0x11471f0`.
- Paths for `ObjectEditorDialog.qml`, `PreviewMonitor.qml`, the EasyFx shell, header and individual effects occur directly in the executable.
- The executable contains `qrc:/_resource/`, `resource.rcc`, native view-model type names, and imports for MAGIX’s namespaced `QResource::registerResource` and `QQmlContext::setContextProperty`.

These are **static references, not runtime load traces or proof of reachability**. No direct `ParameterReflection.qml` or `VxControl_MiniTimeline.qml` filename was found in that executable; the mini-timeline can be reached by another QML component, so absence of its literal name is not proof of non-use.

The resource declares the `VxControls` module and custom singleton/types. Installed native modules also include `com.magix.vxcontrolsplugin` / `VxControlsPlugin.dll`. A generic QML viewer would not automatically receive the EXE’s context objects, lifecycle or selected-object models.

## Loading and the smallest useful future experiment

Qt documents dynamic registration of binary RCC resources. That explains the observed API import, but does not establish MAGIX’s path resolution, accepted replacement resources, registration precedence or reload behavior. Qt’s archived 5.15 documentation is framework background, not a specification of MAGIX’s customized 5.7 build. [Qt QResource documentation](https://doc.qt.io/archives/qt-5.15/qresource.html#dynamic-resource-loading)

No loose-file override, user extension directory, command-line resource switch or independent panel registration mechanism has been established here. Copying a modified resource into Buddy’s directory therefore has no demonstrated route into MAGIX.

A concrete future mutation test should change **one noninteractive label only** in a copied, fully reconstructed resource—preferably the object-editor dialog, whose path is directly referenced by this EXE. Keep object names, imports, dimensions, native bindings and signals unchanged. Validate the rebuilt resource by comparing every entry hash, allowing only the intended QML entry to differ. Use the original format/compression supported by this Qt build.

Before any host test, establish which resource path the host actually registers and whether an isolated application copy can run correctly under Steam. If there is no supported alternate resource path, replacing the installed RCC would be an explicit installation mutation requiring its own backup/restore plan and authorization. This investigation neither performed that mutation nor proved a copied-resource launch path.

Success would mean the harmless label appears when the user opens that dialog. It would prove that one private UI resource can be substituted; it would not yet prove a new dockable panel, timeline enumeration, stable card/object linkage or safe project edits. Only after that could a read-only field such as the current movie name be tested before attempting a Buddy canvas.

For the product design, retain the companion board as the functioning route. Keep this private-QML experiment distinct from OFX effect parameters and from the saved-project reader. It could eventually provide richer host context, but the selected-object and preview bindings do not justify marking planning tasks complete automatically.
