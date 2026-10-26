# productionv2 — Replace SixLabors.ImageSharp with SkiaSharp

**AI coding agent execution plan v2 — FINAL REVIEWED**

**Plan status:** APPROVED FOR IMPLEMENTATION  
**Repository / branch:** `mokth/net10projectTemplate` / `productionv2`  
**Source pin re-verified:** `7afdd5c4058b506fa0978e98943b7d2b70639e20`  
**Source pin date:** 2026-10-26  
**Target framework:** `.NET 10`  
**Primary deployment:** Windows Server / IIS  
**Target dependency:** `SkiaSharp 4.153.1`  
**Migration goal:** Remove the direct `SixLabors.ImageSharp` dependency while preserving the current Item Master image-upload behavior and security contract.

> Final-review conclusion: the migration is technically appropriate for this repository because current production use is narrow and isolated. The migration must remain narrow. Do not redesign Item Master images, storage, UI, database entities, authorization, gallery behavior, or cleanup.

---

# 0. FINAL REVIEW CHANGES FROM v1

The v1 direction was sound, but this final review tightens these points:

1. **Use byte-backed `SKData` for codec inspection**
   - Avoid ambiguous stream ownership/rewind behavior.
   - Prefer `SKData.CreateCopy(source)` followed by `SKCodec.Create(data)`.
   - The bounded upload is already held as a managed `byte[]`, so this is deterministic and simple.

2. **Do not treat Skia `FrameCount` like ImageSharp `FrameCount`**
   - SkiaSharp documents that a still single-frame image may report `FrameCount == 0`.
   - `FrameInfo` may also be empty for still images.
   - Animated detection must be proven with real animated fixtures.
   - Reject any confirmed multi-frame image; do not silently keep frame zero.

3. **Require a complete decode**
   - Do not accept a partially decoded/truncated image.
   - Prefer explicit codec decode result checking and require `SKCodecResult.Success`.
   - `IncompleteInput`, `ErrorInInput`, `InvalidInput`, etc. must not produce a stored image.

4. **Cancellation with Skia is stage-boundary cancellation**
   - Core Skia decode/resize/encode APIs are synchronous.
   - Call `cancellationToken.ThrowIfCancellationRequested()` between expensive stages.
   - Do not claim that an in-progress native resize/encode can be interrupted mid-call.
   - Do not add `Task.Run` merely to make synchronous image processing look asynchronous.

5. **Metadata stripping verification is broader than EXIF**
   - The existing service clears EXIF, XMP, IPTC, and ICC.
   - WebP validation should inspect at least `EXIF`, `XMP `, and `ICCP` RIFF chunks.
   - Do not copy source metadata into output.

6. **Native dependency and license compliance are explicit gates**
   - `SkiaSharp 4.153.1` has an explicit .NET 10 target and depends on `SkiaSharp.NativeAssets.Win32`.
   - Do not add a redundant direct native-assets package unless publish evidence requires it.
   - Run package vulnerability checks.
   - Preserve required MIT/BSD third-party notices in the ERP's dependency/notices process.

These items are mandatory in v2.

---

# 1. REPOSITORY-GROUNDED CURRENT STATE

Current direct dependency:

`ErpWeb.Core/ErpWeb.Core.csproj`

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

Current production usage is concentrated in:

`ErpWeb.Core/Inventory/IvStockMasterImageService.cs`

Current tests:

`ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs`

Current production behavior in `PrepareAsync(...)`:

1. Reject empty upload.
2. Enforce `ItemImages:MaxUploadBytes`.
3. Read the stream using the existing bounded-read protection.
4. Inspect the **actual encoded image**, not filename extension or client MIME.
5. Accept only:
   - JPEG
   - PNG
   - WebP
6. Reject excessive encoded dimensions:
   - `MaxInputSide`
   - `MaxInputPixels`
7. Reject animated/multi-frame inputs.
8. Decode exactly one image.
9. Apply EXIF orientation before metadata removal.
10. Strip source metadata.
11. Resize proportionally to `MaxDimension`.
12. Never upscale.
13. Encode as WebP.
14. Use `ItemImages:WebpQuality`.
15. Enforce `MaxProcessedBytes`.
16. Return:
   - managed `byte[]`
   - final width
   - final height
   - `image/webp`

Current storage/options contract:

```text
RootPath              = App_Data/item-images
MaxUploadBytes        = 8 MB
MaxInputPixels        = 25,000,000
MaxInputSide          = 12,000
MaxDimension          = 1024
MaxProcessedBytes     = 4 MB
WebpQuality           = 85
MaxImagesPerItem      = 6
MaxPendingGalleryBytes= 12 MB
```

Managed file shape remains:

```text
App_Data/item-images/<hashed-company>/<hashed-item>/<guid>.webp
```

No database migration is required.

---

# 2. TARGET DEPENDENCY

Replace:

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

with:

```xml
<PackageReference Include="SkiaSharp" Version="4.153.1" />
```

Reason for the pinned version:

- stable release,
- explicit .NET 10 target,
- MIT-licensed managed package,
- current stable line reviewed for this plan,
- Windows native assets are supplied through its dependency graph.

Do **not** use:

- preview / RC packages,
- `SkiaSharp.Views.*`,
- `SkiaSharp.HarfBuzz`,
- MAUI packages,
- WPF/WinForms integration packages,
- GPU APIs.

Do not add an explicit:

```xml
<PackageReference Include="SkiaSharp.NativeAssets.Win32" ... />
```

unless the actual Release publish/runtime test demonstrates that the transitive dependency is insufficient.

---

# 3. NON-GOALS

Do not change:

- `IvStockMasterImage` schema.
- item-image DB records.
- existing `.webp` files.
- path hashing.
- file naming.
- storage root semantics.
- authorization.
- tenant/company isolation.
- path traversal protection.
- temp-file/atomic-write behavior.
- gallery ordering.
- maximum image count.
- UI.
- API/service signatures.
- cleanup behavior.
- Item Master business logic.
- `ItemImageStorageOptions` names/defaults unless a proven compatibility issue requires it.

Do not create a general media framework.

---

# PHASE 0 — SOURCE PIN, BASELINE, AND AUDIT

## 0.1 Verify source

Before coding:

```bash
git status
git rev-parse HEAD
```

If HEAD differs from:

```text
7afdd5c4058b506fa0978e98943b7d2b70639e20
```

inspect all image-service/package/test changes before implementation.

Record the implementation HEAD.

## 0.2 Repository-wide dependency audit

Search the current branch for:

```text
SixLabors
ImageSharp
Image<Rgba32>
WebpEncoder
JpegEncoder
PngEncoder
ExifProfile
ExifTag
```

Expected known locations:

```text
ErpWeb.Core/ErpWeb.Core.csproj
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs
```

Do not assume the list is complete if HEAD changed.

## 0.3 Baseline build/test

At minimum:

```bash
dotnet build ErpWeb.Core/ErpWeb.Core.csproj -c Release
dotnet build ErpWeb/ErpWeb.csproj -c Release

dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj \
  --filter "FullyQualifiedName~IvStockMasterImageServiceTests"
```

Record all baseline failures before edits.

## 0.4 Package security baseline

Run:

```bash
dotnet list ErpWeb/ErpWeb.csproj package --include-transitive
dotnet list ErpWeb/ErpWeb.csproj package --vulnerable --include-transitive
```

Record the result.

### Phase 0 gate

PASS only if:

- source pin checked,
- all ImageSharp compiled usages identified,
- baseline image tests recorded,
- Release builds recorded,
- package vulnerability baseline recorded.

---

# PHASE 1 — PACKAGE REPLACEMENT

In:

`ErpWeb.Core/ErpWeb.Core.csproj`

remove:

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

add:

```xml
<PackageReference Include="SkiaSharp" Version="4.153.1" />
```

Restore/build immediately.

Skia types must remain private implementation details of the image service.

Do not expose:

```text
SKData
SKCodec
SKBitmap
SKImage
SKSurface
SKPixmap
```

from application interfaces, DTOs, entities, or UI.

### Phase 1 gate

PASS only if Core restores/builds and no unnecessary Skia packages were introduced.

---

# PHASE 2 — SAFE ENCODED-CONTENT INSPECTION

This phase replaces ImageSharp `IdentifyAsync`.

## 2.1 Preserve the existing bounded read

Keep the current:

```text
ReadBoundedAsync(...)
MaxUploadBytes
```

behavior.

Do not hand the original unbounded request stream directly to Skia.

## 2.2 Use deterministic byte-backed codec input

Preferred ownership model:

```csharp
using var encodedData = SKData.CreateCopy(source);
using var codec = SKCodec.Create(encodedData);
```

Use the actual APIs available in the pinned package.

Reason:

- no dependency on caller stream seekability,
- no accidental disposal of caller-owned streams,
- no rewind ambiguity after frame metadata inspection,
- simple native lifetime ownership.

Do not create unmanaged views over temporary managed memory without a proven lifetime contract.

## 2.3 Validate actual format

Use `codec.EncodedFormat`.

Allow only:

```text
SKEncodedImageFormat.Jpeg
SKEncodedImageFormat.Png
SKEncodedImageFormat.Webp
```

Reject all other formats.

Do not trust:

- filename extension,
- browser/client MIME,
- caller-provided `contentType`.

Preserve current user-facing semantics:

Unsupported valid raster:

```text
Only JPG, PNG, or WebP images are allowed.
```

Invalid/corrupt bytes:

```text
The selected file is not a valid image.
```

## 2.4 Header size validation before full decode

Use codec information before allocating full decoded pixel storage.

Require:

```text
width > 0
height > 0
width  <= MaxInputSide
height <= MaxInputSide
(long)width * height <= MaxInputPixels
```

Cast before multiplication.

Do not full-decode an image that already fails these limits.

### Phase 2 gate

Tests prove:

- JPEG accepted,
- PNG accepted,
- WebP accepted,
- fake extension rejected,
- fake MIME rejected,
- random/truncated invalid data rejected,
- BMP/GIF/other raster rejected as unsupported,
- SVG rejected,
- side limit enforced pre-decode,
- pixel limit enforced pre-decode.

---

# PHASE 3 — ANIMATED / MULTI-FRAME REJECTION

Current ImageSharp behavior rejects multi-frame images. Preserve it.

## 3.1 Important Skia semantics

Do **not** write logic assuming:

```text
single-frame => FrameCount == 1
```

SkiaSharp documentation states that still single-frame codecs may report:

```text
FrameCount == 0
FrameInfo.Length == 0
```

Therefore write detection based on confirmed additional frames, not equality to one.

At minimum, inspect:

```text
codec.FrameCount
codec.FrameInfo
```

using the complete byte-backed input.

Reject a confirmed multi-frame image.

Do not use `RepetitionCount != 0` as the only animation test because an animation that plays once may also have repetition count zero.

## 3.2 Fixture proof is mandatory

Add a real tiny animated WebP fixture.

Preferred:

```text
ErpWeb.Tests/Fixtures/Images/animated-2frame.webp
```

or deterministic base64 fixture bytes.

Do not generate the animated test input at test runtime using the same production codec.

Recommended additional fixture:

```text
animated.png
```

if Skia recognizes APNG as multi-frame in the pinned version.

### Phase 3 gate

PASS only if the real animated WebP fixture is rejected.

A test that only mocks frame count is insufficient.

---

# PHASE 4 — STRICT SINGLE-FRAME DECODE

Do not accept partial/truncated decode.

## 4.1 Decode into owned bitmap memory

Create one destination bitmap with a safe supported color/alpha representation.

Use a codec decode operation where the result can be checked.

Preferred direction:

```text
codec.GetPixels(...)
```

into owned `SKBitmap` memory.

Require:

```text
SKCodecResult.Success
```

Anything else is a failure, including:

```text
IncompleteInput
ErrorInInput
InvalidInput
InvalidConversion
InvalidParameters
CouldNotRewind
InternalError
Unimplemented
```

Do not store a partially generated image merely because Skia produced some pixels.

If the exact pinned API makes a different complete-decode path safer, document it and prove truncated input is rejected.

## 4.2 Cancellation boundaries

Skia decode is synchronous.

Before decode:

```csharp
cancellationToken.ThrowIfCancellationRequested();
```

After decode:

```csharp
cancellationToken.ThrowIfCancellationRequested();
```

Do not wrap codec work in `Task.Run`.

Do not claim mid-native-call cancellation.

### Phase 4 gate

PASS only if:

- normal inputs decode,
- deliberately truncated JPEG/PNG/WebP inputs fail,
- cancellation requested before decode propagates as `OperationCanceledException`.

---

# PHASE 5 — EXIF ORIENTATION NORMALIZATION

This is the highest-risk behavioral parity item.

Current behavior:

```csharp
image.Mutate(x => x.AutoOrient());
```

must be preserved.

## 5.1 Read encoded origin

Use:

```text
codec.EncodedOrigin
```

Support all 8 origin values:

```text
TopLeft
TopRight
BottomRight
BottomLeft
LeftTop
RightTop
RightBottom
LeftBottom
```

Conceptual EXIF behaviors:

```text
1 TopLeft      normal
2 TopRight     horizontal mirror
3 BottomRight  180° rotation
4 BottomLeft   vertical mirror
5 LeftTop      transpose
6 RightTop     90° clockwise
7 RightBottom  transverse
8 LeftBottom   270° clockwise
```

Do not support only orientations 1/3/6/8 and silently mishandle mirror cases.

## 5.2 Correct processing order

Required:

```text
inspect
→ decode
→ normalize encoded orientation
→ derive normalized dimensions
→ resize if required
→ encode WebP
```

Orientations 5–8 swap width/height.

## 5.3 Deterministic fixture tests

Do not keep ImageSharp just to create EXIF fixtures.

Use committed/embedded tiny JPEG fixture bytes.

Preferred coverage:

```text
orientation-1.jpg
orientation-2.jpg
...
orientation-8.jpg
```

If all eight fixtures are small, test all eight.

For mirrored/rotated cases, assert actual corner pixel placement, not dimensions only.

### Phase 5 gate

PASS only when all eight encoded-origin mappings are proven or the agent explains why an origin cannot occur with the accepted codecs and provides evidence.

---

# PHASE 6 — RESIZE WITHOUT UPSCALING

Preserve current ImageSharp `ResizeMode.Max` behavior.

Given normalized dimensions:

```text
sourceWidth
sourceHeight
MaxDimension
```

calculate proportional target dimensions with:

```text
scale <= 1
longest output side <= MaxDimension
```

Examples:

```text
2048x1024 -> 1024x512
1024x2048 -> 512x1024
800x600   -> 800x600
80x40     -> 80x40
```

Avoid floating-point rounding that can produce:

```text
1025
0
```

as an output dimension.

## 6.1 Sampling

Use the supported SkiaSharp sampling API from the pinned version, such as:

```text
SKSamplingOptions
SKBitmap.Resize(...)
```

or:

```text
ScalePixels(...)
```

Use a high-quality CPU sampling option appropriate for photos.

Do not use GPU APIs.

Do not use obsolete quality enums if the pinned API provides `SKSamplingOptions`.

## 6.2 Cancellation boundary

Check cancellation immediately before and after resize.

### Phase 6 gate

Tests cover:

- landscape,
- portrait,
- square,
- no-upscale,
- tiny images,
- transparent image,
- oriented image followed by resize.

---

# PHASE 7 — WEBP ENCODE

Every accepted input remains normalized to:

```text
ContentType = image/webp
Extension   = .webp
```

Use the pinned Skia encoder, conceptually:

```csharp
image.Encode(SKEncodedImageFormat.Webp, _options.WebpQuality)
```

or the equivalent supported bitmap encode overload.

`WebpQuality` remains configuration driven.

Current valid range remains:

```text
1..100
```

Do not hard-code 85.

## 7.1 Alpha

Preserve alpha/transparency.

Add a transparent PNG test with known transparent and opaque pixels.

Decode the resulting WebP and verify alpha behavior.

## 7.2 Processed byte limit

After encode:

```text
output length > 0
output length <= MaxProcessedBytes
```

otherwise preserve:

```text
The processed image is too large.
```

## 7.3 Quality testing

Do not use brittle assertions like:

```text
quality 100 must always be exactly N bytes
```

or assume a fixed compression ratio.

Instead:

- unit-test that configured quality is passed to the encoding helper,
- use a nontrivial fixture to prove different configured quality values produce valid WebP,
- only assert byte difference/size relationship if stable for the pinned version.

### Phase 7 gate

PASS only if WebP output, dimensions, alpha, quality plumbing, and byte-limit behavior are proven.

---

# PHASE 8 — METADATA STRIPPING

Current ImageSharp implementation clears:

```text
EXIF
XMP
IPTC
ICC
```

Preserve that privacy/security intent.

The Skia path must:

```text
decode pixels
→ create normalized output image from pixels
→ encode a new WebP
```

Do not copy encoded source metadata/chunks.

## 8.1 WebP chunk verification

Add a tiny test-only RIFF/WebP chunk reader.

For output, assert there is no source metadata chunk such as:

```text
EXIF
XMP 
ICCP
```

Do not add another large image library only to inspect metadata.

The source EXIF-orientation fixture must prove:

- pixels are correctly normalized,
- orientation metadata is not required afterward,
- source EXIF does not survive.

If the Skia encoder emits an ICC profile from the output color-space configuration, choose an output representation that preserves current service behavior (source ICC removed) and verify the resulting WebP.

### Phase 8 gate

PASS only if source metadata is demonstrably not propagated.

---

# PHASE 9 — ERROR MAPPING, CANCELLATION, AND RESOURCE SAFETY

## 9.1 Cancellation

`OperationCanceledException` must propagate.

Place:

```csharp
cancellationToken.ThrowIfCancellationRequested();
```

between:

```text
bounded read
codec inspection
decode
orientation
resize
encode
```

Skia native operations themselves are synchronous; do not promise finer cancellation.

## 9.2 Error mapping

Do not expose:

- native pointer information,
- server paths,
- Skia internal errors,
- stack traces

to ERP users.

Expected user-level categories remain:

```text
empty
upload too large
unsupported format
invalid/corrupt image
dimensions too large
processed output too large
```

Unexpected errors should be logged appropriately.

Do not broadly catch and suppress:

```text
OutOfMemoryException
StackOverflowException
AccessViolationException
```

as ordinary validation errors.

## 9.3 Native disposal

Deterministically dispose all native objects, including as applicable:

```text
SKData
SKCodec
SKBitmap
SKImage
SKSurface
SKPixmap
encoded SKData
```

Avoid double-disposal of shared-owned native objects.

Do not return a stream or memory view whose native owner has already been disposed.

The existing return type is managed `byte[]`; keep that safe boundary.

### Phase 9 gate

Run repeated processing loops and ensure there is no native-handle/disposal failure.

---

# PHASE 10 — TEST MIGRATION

Update:

`ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs`

Remove:

```text
SixLabors.ImageSharp.*
```

The final test project must not retain ImageSharp simply to manufacture fixtures.

## 10.1 Runtime-generated simple fixtures

SkiaSharp may be used in tests to create simple:

```text
JPEG
PNG
WebP
```

files for ordinary happy-path tests.

That is acceptable.

## 10.2 Independent fixed fixtures

Use fixed committed/embedded bytes for behaviors where production-code independence matters:

```text
EXIF orientation 1..8
animated WebP
truncated/corrupt files
metadata-bearing JPEG
optional APNG
```

This avoids testing Skia solely against files generated by the same API under test.

## Required regression matrix

### Input

- JPEG accepted.
- PNG accepted.
- WebP accepted.
- extension spoof rejected.
- MIME spoof rejected.
- random bytes rejected.
- truncated JPEG rejected.
- truncated PNG rejected.
- truncated WebP rejected.
- GIF rejected.
- SVG rejected.
- animated WebP rejected.
- empty rejected.
- `MaxUploadBytes` enforced.
- `MaxInputSide` enforced.
- `MaxInputPixels` enforced.

### Processing

- all 8 EXIF origins.
- landscape resize.
- portrait resize.
- square resize.
- no-upscale.
- aspect ratio.
- transparent alpha.
- WebP output.
- configured quality plumbing.
- `MaxProcessedBytes`.
- metadata stripped.

### Existing storage/security tests

Keep unchanged:

- managed relative path.
- hash path.
- traversal rejection.
- absolute-path rejection.
- idempotent cleanup.
- no orphan temp file after store failure.
- company isolation.
- access-right enforcement.

Do not remove security tests because they do not involve Skia.

---

# PHASE 11 — REPOSITORY CLEANUP

After migration:

```bash
git grep -n "SixLabors"
git grep -n "ImageSharp"
```

No compiled product/test use should remain.

Then:

```bash
dotnet list ErpWeb.Core/ErpWeb.Core.csproj package
dotnet list ErpWeb/ErpWeb.csproj package --include-transitive
dotnet list ErpWeb/ErpWeb.csproj package --vulnerable --include-transitive
```

Required result:

- no direct `SixLabors.ImageSharp`,
- no unexpected vulnerable package introduced by this migration,
- `SkiaSharp` version is the approved stable pin.

If another unrelated dependency still pulls ImageSharp transitively, report it explicitly instead of claiming total graph removal.

---

# PHASE 12 — THIRD-PARTY LICENSE NOTICE

This migration is being performed partly to avoid the Six Labors commercial licensing model.

Do not replace that problem with sloppy open-source notice handling.

Verify the ERP's existing third-party notices/license process.

At minimum record:

```text
SkiaSharp — MIT
Skia / native graphics engine — applicable upstream BSD-style notices
```

Preserve the copyright/license notices required by distributed dependencies.

Do not claim "no license obligations"; the correct statement is that SkiaSharp is permissively licensed, but its required notices still apply.

This is a compliance gate, not a source-code behavior change.

---

# PHASE 13 — WINDOWS SERVER / IIS PUBLISH PROOF

SkiaSharp contains native code. Unit tests alone are insufficient.

## 13.1 Publish the actual host

Test the same publish model used by the ERP.

Also perform an explicit x64 validation publish, for example:

```bash
dotnet publish ErpWeb/ErpWeb.csproj \
  -c Release \
  -r win-x64 \
  --self-contained false
```

Do not change the production deployment model merely to satisfy this test.

Verify publish output contains the required Windows Skia native asset, normally:

```text
libSkiaSharp.dll
```

in the appropriate runtime/native output structure or flattened publish output.

Verify:

- no `DllNotFoundException`,
- no `BadImageFormatException`,
- x64 app pool/process matches deployment bitness,
- application startup succeeds.

Do not fix native-load errors by manually downloading random DLLs.

Fix PackageReference/RID/publish configuration instead.

## 13.2 IIS smoke test

On representative IIS:

1. upload JPEG;
2. upload PNG;
3. upload WebP;
4. upload a phone/camera EXIF-rotated JPEG;
5. upload >1024 image;
6. upload small image;
7. reopen image;
8. replace image;
9. delete image;
10. restart app pool;
11. reopen previously stored WebP.

Existing WebP files must remain readable without migration.

### Phase 13 gate

PASS only when Release/IIS native loading and image workflow succeed.

---

# PHASE 14 — PERFORMANCE / MEMORY SMOKE

Test:

```text
80x40
typical phone photo
2048x1024
near MaxInputPixels
near MaxUploadBytes
corrupt input
animated input
```

Record approximately:

```text
input bytes
input dimensions
output dimensions
output bytes
elapsed prepare time
```

Do not turn this into a benchmark project unless a real regression appears.

Security invariants:

- byte limit before expensive image work,
- dimension/pixel limit before full decode,
- no unbounded image allocation,
- no filename-derived filesystem path,
- no source metadata propagation.

---

# PHASE 15 — FINAL ACCEPTANCE CHECKLIST

Migration is complete only when all items pass:

- [ ] source HEAD recorded.
- [ ] `SixLabors.ImageSharp` removed from `ErpWeb.Core.csproj`.
- [ ] `SkiaSharp 4.153.1` used.
- [ ] no preview/RC Skia package.
- [ ] no unnecessary direct native-assets package.
- [ ] Core Release build passes.
- [ ] ERP host Release build passes.
- [ ] image-service tests pass.
- [ ] actual JPEG/PNG/WebP detection passes.
- [ ] extension/MIME spoofing remains rejected.
- [ ] GIF/SVG remain rejected.
- [ ] real animated WebP is rejected.
- [ ] truncated input is rejected; partial decode is never stored.
- [ ] byte/side/pixel limits remain enforced pre-decode.
- [ ] all 8 EXIF origin mappings are validated.
- [ ] no-upscale behavior preserved.
- [ ] aspect ratio preserved.
- [ ] WebP quality remains config-driven.
- [ ] alpha preserved.
- [ ] `MaxProcessedBytes` enforced.
- [ ] source EXIF/XMP/ICC metadata not propagated.
- [ ] cancellation propagates at stage boundaries.
- [ ] native Skia objects are deterministically disposed.
- [ ] storage/path/security/cleanup tests remain green.
- [ ] compiled source/tests contain no ImageSharp reference.
- [ ] package vulnerability check reviewed.
- [ ] third-party license notices reviewed.
- [ ] Windows x64 publish contains working native assets.
- [ ] IIS smoke test passes.
- [ ] existing stored WebP files still open.
- [ ] no database migration introduced.
- [ ] no unrelated Item Master/UI behavior changed.

---

# 16. RECOMMENDED IMPLEMENTATION SHAPE

Keep the change inside the existing service with small private helpers.

Conceptually:

```text
IvStockMasterImageService.PrepareAsync
    |
    +-- ReadBoundedAsync
    |
    +-- Create SKData from bounded byte[]
    |
    +-- InspectEncodedImage
    |      +-- actual format
    |      +-- encoded dimensions
    |      +-- frame metadata
    |      +-- encoded origin
    |
    +-- DecodeSingleFrameStrict
    |      +-- require SKCodecResult.Success
    |
    +-- NormalizeOrientation
    |
    +-- CalculateTargetSize
    |
    +-- ResizeIfRequired
    |
    +-- EncodeWebp
    |
    +-- Validate MaxProcessedBytes
    |
    +-- return managed IvPreparedStockImage
```

Reasonable private helpers:

```text
TryInspectImage(...)
IsAllowedEncodedFormat(...)
DecodeSingleFrameStrict(...)
NormalizeOrientation(...)
CalculateTargetSize(...)
ResizeIfRequired(...)
EncodeWebp(...)
```

Do not create a second public image-service abstraction unless current repository design genuinely requires one.

---

# 17. EXPECTED FILES TO CHANGE

Primary:

```text
ErpWeb.Core/ErpWeb.Core.csproj
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs
```

Optional tiny fixtures:

```text
ErpWeb.Tests/Fixtures/Images/*
```

Potential third-party notice file only if the repository already has or needs one:

```text
THIRD-PARTY-NOTICES*
```

Normally unchanged:

```text
ErpWeb.Core/Inventory/ItemImageStorageOptions.cs
database entities
database scripts
UI files
```

If implementation requires broad changes outside this boundary, stop and explain the reason.

---

# 18. DO NOT DO THESE

Do **not**:

- use `System.Drawing.Common`;
- trust extension/MIME;
- remove bounded upload reading;
- full-decode before checking dimensions;
- accept a partial/truncated decode;
- assume Skia single-frame `FrameCount == 1`;
- use `RepetitionCount` alone to detect animation;
- silently keep frame zero of animated input;
- ignore mirrored EXIF origins;
- resize before orientation normalization;
- upscale small images;
- change output away from WebP;
- hard-code quality;
- copy source EXIF/XMP/ICC metadata;
- add a large metadata library just for tests;
- use GPU rendering;
- introduce `Task.Run` just to wrap Skia CPU work;
- leak Skia native objects;
- change storage paths;
- rewrite DB/gallery/UI logic;
- retain ImageSharp in tests and claim removal is complete;
- add preview/RC Skia packages;
- manually deploy arbitrary native DLLs;
- skip IIS validation;
- claim there are "no license obligations" simply because the replacement is MIT licensed.

---

# 19. AGENT FINAL REPORT FORMAT

Report exactly:

1. **Source HEAD implemented against**
2. **Files changed**
3. **Package changes**
4. **ImageSharp direct references remaining**
5. **ImageSharp transitive references remaining**
6. **SkiaSharp/native versions resolved**
7. **Behavior preserved**
8. **Strict decode/result handling**
9. **Animated-image detection and fixture proof**
10. **EXIF orientation coverage**
11. **Metadata stripping proof**
12. **WebP output/quality/alpha proof**
13. **Cancellation/resource-disposal proof**
14. **Focused test result**
15. **Affected broader test result**
16. **Release build result**
17. **Package vulnerability result**
18. **License-notice result**
19. **Windows x64 publish/native asset result**
20. **IIS smoke-test result**
21. **Known output differences**, if any
22. **FINAL RESULT: READY / NOT READY**

Do not report READY because compilation alone succeeds.

---

# 20. APPROVAL

**FINAL REVIEW STATUS: APPROVED FOR IMPLEMENTATION**

The current repository uses ImageSharp only for a narrow Item Master upload/normalization path. Replacing it with SkiaSharp is reasonable and should remain low-risk if the agent preserves the existing security and behavioral contracts.

This approval is for implementation, not automatic Production deployment.

Production deployment is approved only after:

```text
focused tests
+ Release build
+ vulnerability review
+ license-notice review
+ Windows x64 publish
+ IIS smoke test
```

all pass.

---

# 21. VERIFIED EXTERNAL FACTS FOR THIS PLAN

Reviewed on 2026-10-26:

- `SkiaSharp 4.153.1` is a stable release.
- NuGet lists explicit `net10.0` compatibility/targeting.
- NuGet lists `SkiaSharp.NativeAssets.Win32 >= 4.153.1` as a dependency for `net10.0`.
- SkiaSharp is published under the MIT license.
- `SKCodec` exposes `EncodedFormat`, `EncodedOrigin`, `FrameCount`, `FrameInfo`, and `RepetitionCount`.
- SkiaSharp documentation notes that single-frame images may have `FrameCount == 0`.
- `SKCodec.GetPixels(...)` returns an `SKCodecResult`, including `Success`, `IncompleteInput`, `ErrorInInput`, and `InvalidInput`.
- `SKImage.Encode(SKEncodedImageFormat, int)` accepts a quality value in the 0–100 range.
- `SKBitmap.Resize` / `ScalePixels` support `SKSamplingOptions`.

References:

- https://www.nuget.org/packages/SkiaSharp/4.153.1
- https://github.com/mono/SkiaSharp/releases
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec.framecount
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec.getpixels
- https://learn.microsoft.com/dotnet/api/skiasharp.skimage.encode
- https://learn.microsoft.com/dotnet/api/skiasharp.skbitmap.resize
