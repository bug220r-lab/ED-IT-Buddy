# Exact-install OFX host: static capability evidence

Inspected 2026-10-29. This supersedes the earlier string-presence-only assessment of standard OFX drawing support. It does not replace runtime testing.

## Result that changes the implementation plan

The installed wrapper's host initialization explicitly writes **0** to both `OfxParamHostPropSupportsCustomInteract` and `OfxImageEffectPropSupportsOverlays`. This is stronger evidence than the mere existence of those strings or the generic default-property table. A standard OFX custom parameter canvas or preview overlay should therefore **not be the assumed route** for the Buddy whiteboard in this build.

The wrapper does return an **OFX timeline suite version 1** with three non-null callback pointers. These callbacks forward to virtual methods on the effect instance; this investigation has not resolved their concrete implementations or proved useful live behavior. It does not establish access to all tracks, clips, or the project graph.

Ordinary reflected plug-in controls, the proprietary MAGIX UI interfaces, and a separate Buddy window remain distinct integration paths. The findings here do not rule those out.

## Target identity and method

- Path: `C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition\ImageProcessingPlugins\MxVfxOfxWrapper_x64.dll`
- File description: `MAGIX OpenFX Wrapper Plugin`
- File/product version: `1.0.15.3582`
- Size: `884736` bytes.
- SHA-256: `02bffacb5657bd623b846b68690f47f0ea30f3416036e7b519ff8d3a8f77e570`
- PE image base: `0x180000000`; addresses below are RVAs unless explicitly marked as file offsets.
- Tool: [ofx_inspect_host.py](../tools/ofx_inspect_host.py), using the separately approved `iced-x86 1.21.0` decoder. Reads the target as bytes; never loads or executes it.
- Instruction boundaries for the relevant non-leaf functions were taken from the PE exception directory (`.pdata`), then decoded from the function start. The short leaf callback at `0x6BD50` has no `.pdata` record and was decoded over its exact 21-byte body.

No MAGIX window/process inspection, plug-in installation, target DLL execution, target modification, or source-project modification was performed.

## Capability defaults versus actual initialization

The installed DLL has 32-byte property-spec records matching the layout in the official [OpenFX HostSupport property header](https://github.com/AcademySoftwareFoundation/openfx/blob/main/HostSupport/include/ofxhPropertySuite.h): name pointer, property type, dimension, read-only flag, default-value string. Integer type is `0`.

| Property | String RVA | Spec file offset | Table default | Explicit initialization |
| --- | --- | --- | --- | --- |
| Custom parameter interaction | `0x8A8E0` | `0xC64F0` | integer `0`, dimension 1 | Writes integer `0` at property index 0 |
| Preview overlays | `0x8A750` | `0xC6390` | integer `1`, dimension 1 | **Overrides default to integer `0`** at property index 0 |

These defaults also match the generic [OpenFX HostSupport host-properties array](https://github.com/AcademySoftwareFoundation/openfx/blob/main/HostSupport/src/ofxhImageEffect.cpp). The actual MAGIX initializer is therefore essential: reading only the generic overlay default would lead to the wrong inference.

### Initializer proof

Both property-setting sequences are in the same function: **`[0x39870, 0x3AD90)`**. Its caller at `0x3979E` is inside function `[0x39460, 0x397DA)`.

The initializer begins by zeroing R15D at `0x398B0`. Focused decoding of the entire function found no later assignment changing R15/R15D before its final stack restore. The intervening calls must preserve R15 under the x64 calling convention. The property-setting calls use the same property set address (`0x1800D0268`) and setter (`0x18005B260`).

```text
000398B0  xor r15d,r15d

; Preview overlays: string RVA 0x8A750
00039CB3  lea rdx,[18008A750h]
00039CBA  lea rcx,[rbp+0A0h]
00039CC1  call 0000000180003460h ; constructs the property-name string
00039CC7  mov [rbp+70h],r15d    ; value = 0
00039CCB  lea r9,[rbp+70h]     ; pointer to integer value
00039CCF  xor r8d,r8d          ; property index = 0
00039CD2  lea rdx,[rbp+0A0h]   ; property name
00039CD9  lea rcx,[1800D0268h] ; property set
00039CE0  call 000000018005B260h

; Custom parameter interaction: string RVA 0x8A8E0
0003A600  lea rdx,[18008A8E0h]
0003A607  lea rcx,[rsp+30h]
0003A60C  call 0000000180003460h
0003A612  mov [rbp+78h],r15d    ; value = 0
0003A616  lea r9,[rbp+78h]
0003A61A  xor r8d,r8d
0003A61D  lea rdx,[rsp+30h]
0003A622  lea rcx,[1800D0268h]
0003A629  call 000000018005B260h
```

Setter function **`[0x5B260, 0x5B342)`** resolves the named property, casts to the integer-property type, checks/resizes the value vector, and performs this write:

```text
0005B283  mov r15,r9          ; retain incoming value pointer
0005B286  movsxd rdi,r8d      ; retain property index
...
0005B2ED  mov edx,[r15]       ; read incoming integer
0005B2F0  mov rax,[rbx+58h]   ; value vector storage
0005B2F4  mov [rax+rdi*4],edx ; store it at the requested index
```

**Confidence:** high for these initialization writes in this exact DLL. This is not a proof that no later code can ever change a host property: generic setters, indirect construction, other DLLs, or alternate execution paths could do so. No runtime capability query was performed. The inspected direct string references nevertheless provide a substantially stronger reason to avoid assuming standard OFX drawing support.

## Timeline suite: real dispatch and callback table

The string `OfxTimeLineSuite` has RVA `0x90FA0`. Two suite-dispatch functions reference it:

- Function `[0x3AD90, 0x3B0B8)`, name comparison at `0x3AFDF`; version-1 result at `0x3AFF2`.
- Function `[0x6BFB0, 0x6C186)`, name comparison at `0x6C0BC`; version-1 result at `0x6C0D4`.

Both return the table at **RVA `0xC7240`**, file offset **`0xC5A40`**, for version 1, and return null for other requested versions on this branch.

```text
0003AFDF  lea rdx,[180090FA0h]
0003AFE6  mov rcx,rdi
0003AFE9  call 0000000180075F5Fh ; string comparison
0003AFEE  test eax,eax
0003AFF0  jne short 000000018003B004h
0003AFF2  lea rcx,[1800C7240h] ; suite table
0003AFF9  xor edx,edx
0003AFFB  cmp ebx,1           ; requested version
0003AFFE  cmovne rcx,rdx      ; null unless version 1
0003B002  jmp short 000000018003B06Fh
```

The table order matches the [official OfxTimeLineSuiteV1 definition](https://openfx.readthedocs.io/en/main/Reference/suites/ofxTimeLineSuiteReference.html):

| Table file offset | Callback RVA | Meaning by suite position | Observed implementation |
| --- | --- | --- | --- |
| `0xC5A40` | `0x6BD20` | `getTime` | Calls virtual slot `+0x08` on instance subobject `+0x60`, writes returned double to output pointer, returns 0 |
| `0xC5A48` | `0x6BD50` | `gotoTime` | Tail-calls virtual slot `+0x10` on that subobject |
| `0xC5A50` | `0x6BD70` | `getTimeBounds` | Calls virtual slot `+0x18` on that subobject, returns 0 |

All three callbacks return status 9 when the instance pointer is null. Non-null wrappers are not evidence that their downstream implementation successfully queries or changes MAGIX's current timeline.

```text
; getTime, function [0x6BD20, 0x6BD4E)
0006BD20  push rbx
0006BD22  sub rsp,20h
0006BD26  mov rbx,rdx
0006BD29  test rcx,rcx
0006BD2C  jne short 000000018006BD37h
0006BD2E  lea eax,[rcx+9]
0006BD31  add rsp,20h
0006BD35  pop rbx
0006BD36  ret
0006BD37  mov rax,[rcx+60h]
0006BD3B  add rcx,60h
0006BD3F  call qword ptr [rax+8]
0006BD42  movsd [rbx],xmm0
0006BD46  xor eax,eax
0006BD48  add rsp,20h
0006BD4C  pop rbx
0006BD4D  ret

; gotoTime, leaf body [0x6BD50, 0x6BD65)
0006BD50  test rcx,rcx
0006BD53  jne short 000000018006BD59h
0006BD55  lea eax,[rcx+9]
0006BD58  ret
0006BD59  mov rax,[rcx+60h]
0006BD5D  add rcx,60h
0006BD61  jmp qword ptr [rax+10h]

; getTimeBounds, function [0x6BD70, 0x6BD93)
0006BD70  sub rsp,28h
0006BD74  test rcx,rcx
0006BD77  jne short 000000018006BD81h
0006BD79  lea eax,[rcx+9]
0006BD7C  add rsp,28h
0006BD80  ret
0006BD81  mov rax,[rcx+60h]
0006BD85  add rcx,60h
0006BD89  call qword ptr [rax+18h]
0006BD8C  xor eax,eax
0006BD8E  add rsp,28h
0006BD92  ret
```

**Confidence:** high for suite dispatch/table/function bodies; unresolved for the downstream virtual methods and useful project behavior. The standard suite is limited to an effect-associated current time, seek, and time bounds. It does not enumerate tracks or clips.

## Bounded next experiment

When application operation is authorized, use a disposable project and a minimal identity OFX plug-in that logs the two live capability values and whether timeline-suite version 1 is returned. Query time/bounds read-only, with a person changing the playhead to known positions. Do not call unsupported custom-interaction APIs, force capability flags to true, or call private MAGIX interfaces based on guessed signatures.

If live values confirm zero, the standard OFX canvas branch is closed for this host configuration. Investigate the existing reflected control mechanism or a documented/verified proprietary UI entry point; a normal Buddy window can still hold the whiteboard. A non-null suite should be considered useful only after the known-position comparison succeeds.

## Reproduction

From the workspace, run the helper with the absolute DLL path. The default mode prints target strings, property-spec records, hash, and instruction xrefs. `--function-rva 0x39870` prints the initializer, `--function-rva 0x5B260` prints the integer setter, and `--function-rva 0x6BD20` / `0x6BD70` print the two non-leaf callback bodies. Use `--range-rva 0x6BD50 --range-bytes 21` for the short leaf body.

Every interpretation above is tied to the recorded hash. Do not reuse these RVAs for a different binary version.
