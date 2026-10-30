# MAGIX control inspection

Target: Movie Edit Pro 2021 Plus Steam Edition, `Videodeluxe.exe` version `20,0,1,80`, Steam app `1363250`. Inspected October 29, 2026 on this PC.

The first direct executable launch reported missing `DLLAV64.dll` before Steam finished its first-run setup. Launch through Steam succeeded afterward. The companion therefore launches using Steam's app ID.

## Observed interfaces

| Control | Accessible identity | Observed pattern | First-build use |
|---|---|---|---|
| Main editor | Class `MainWindow_Videodeluxe` | Window, Transform | Exact executable/process matching and foreground verification |
| Import tab | Button, ID `10912`, name `Import` | Invoke | Candidate for a verified panel action |
| Templates tab | Button, ID `10913`, name `Templates` | Invoke | Inspection only |
| Effects tab | Button, ID `10915`, name `Effects` | Invoke | Candidate for a verified panel action |
| Audio tab | Button, ID `10916`, name `Audio` | Invoke | Inspection only |
| File menu | Button, ID `5819` | Invoke | Inspection only |
| Effects menu | Button, ID `5821` | Invoke | Distinct from the Effects tab; not used as a substitute |
| Play transport | Button, name `play`, no AutomationId | Invoke | Inspection only; playback verification still required |
| Start navigation | Button, name `goto begin`, no AutomationId | Invoke | Inspection only; timecode verification still required |
| Timecode | Custom, name `timecode` | No useful read/write pattern in this snapshot | No frame-accurate timeline seeking enabled |
| Timeline area | Custom/native workspace | Incomplete object-level semantics in this snapshot | No object edits enabled |
| Missing-media dialog | Window `File not found.`, native `#32770` | Window/Transform; named buttons expose Invoke | Recognized as blocking; no Skip/Skip all action enabled |

The native probe read **117 accessible elements** while the transferred project was waiting on missing media. Some transport children still reported enabled despite their owner being disabled. This is why the controller checks the native main-window/modal state before any panel action, rather than trusting a child button alone.

## Validation record

- Successful release compilation with the installed .NET Framework compiler.
- Main companion launched and displayed real connection state and accessible controls.
- Read-only inspection succeeded while a modal was present.
- Initial focus-only action correctly refused to send input through a blocked editor. The updated focus behavior can bring the active dialog forward without interacting with its contents.
- Review-store tests cover local persistence, source preservation, mapping reset, unsupported imports, duplicate IDs, null times, dismissed notes, UTF-8 size limits, and new-document metadata.

Panel activation and updated focus behavior still require live validation. Panel activation is not executable in the release action allowlist. Follow-up results should be appended here only after observation.

## Offline development validation

After the user requested no computer control, work continued only on files and automated tests. No new live windows were inspected or operated. The app now defaults to offline mode for a new preferences file, defers creating its controller, and requires an explicit connection before querying MAGIX.

Pure controller tests exercise main-window class matching, PID filtering, ambiguous windows, disabled owners, related dialogs, missing observations, the strict action allowlist, and capability evidence. The helper accepts only a read-only inspection request. Panel actions cannot be enabled by an imported note or by supplying an unadvertised action ID.

The target policy matches the observed `MainWindow_Videodeluxe` class. This revised detection and active-dialog focusing have compiled and passed policy tests; their next live run remains to be checked.

## Integration rules

The controller uses a fixed action allowlist and never accepts arbitrary element IDs or keyboard strings from imported notes. UIA work runs in a separate bounded process. A timeout is reported as an uncertain outcome and is not retried automatically. A snapshot is a point-in-time observation; controls are located again immediately before use.
