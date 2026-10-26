# productionv2 — Replace SixLabors.ImageSharp with SkiaSharp

**AI coding agent execution plan v3**

**Plan status:** APPROVED FOR IMPLEMENTATION  
**Repository / branch:** `mokth/net10projectTemplate` / `productionv2`  
**Source pin:** `7afdd5c4058b506fa0978e98943b7d2b70639e20`  
**Source pin date:** 2026-10-26  
**Target framework:** `.NET 10`  
**Primary deployment:** Windows Server / IIS  
**Target dependency:** `SkiaSharp 4.153.1`  
**Migration goal:** Remove the direct `SixLabors.ImageSharp` dependency while preserving the current Item Master image-upload behavior and security contract.

This document is the execution plan. v2 remains historical review evidence and is not edited. Implementing this plan changes the image pipeline. Merely having this file in the repository does not.

The migration stays inside `PrepareAsync` image normalization. Do not redesign Item Master images, storage, UI, database entities, authorization, gallery behavior, or cleanup.

---

# 0. WHAT CHANGED FROM v2

v2's scope was right. These rules replace the v2 instructions that were wrong or incomplete.

1. **Lossy WebP encoder.** Encode with `SKWebpEncoder` and `SKWebpEncoderCompression.Lossy`. Pass `ItemImages:WebpQuality`. `SKImage.Encode(SKEncodedImageFormat.Webp, quality)` is not the production path: Skia treats quality 100 as lossless, while ImageSharp `WebpEncoder` stays lossy at 100.
2. **Strict decode.** `SKCodec.GetPixels` must return `SKCodecResult.Success`. Any other result is a failure. A partial bitmap is never stored.
3. **Null codec.** `SKCodec.Create(SKData)` returns null for unreadable bytes. That maps to `The selected file is not a valid image.` A real codec in any other format maps to `Only JPG, PNG, or WebP images are allowed.`
4. **Animation condition.** Reject when `FrameCount > 1` or `FrameInfo.Length > 1`. A still image may report `FrameCount == 0`. Do not reject `FrameCount != 1`. Do not use `RepetitionCount` alone.
5. **Unpremul is intermediate only.** Decode to `SKColorType.Rgba8888` and `SKAlphaType.Unpremul`. Do not use an Unpremul bitmap as an `SKCanvas` rendering target. Orientation is either direct pixel remapping or a Premul canvas. The final report names which one was used.
6. **Color space.** Create normalized bitmap info with `colorSpace: null` (or the equivalent API). Source ICC must not become output ICC metadata. Tests assert the WebP has no `EXIF`, `XMP `, or `ICCP` chunk.
7. **Fonts.** `SixLabors.Fonts` stays because ClosedXML depends on it. Do not hard-code a resolved version. Record the version from `dotnet list`.
8. **Notices.** Record SkiaSharp MIT and Skia BSD-style notices in the repository's disclosure mechanism, in a new `THIRD-PARTY-NOTICES.txt` when customer-distributed binaries have no mechanism, or as evidence that an external compliance process already supplies them. Do not claim there are no license obligations.

---

# 1. REPOSITORY-GROUNDED CURRENT STATE

Current direct dependency in `ErpWeb.Core/ErpWeb.Core.csproj`:

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

Compiled production use is `ErpWeb.Core/Inventory/IvStockMasterImageService.cs`.

Compiled tests are `ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs`.

`SixLabors.Fonts` is a transitive dependency of `ClosedXML`, which `ErpWeb.Core` references directly. It is not part of ImageSharp. The repository has no `packages.lock.json` and no `Directory.Packages.props` pinning that transitive version. Leave Fonts in place. Record whatever version `dotnet list` resolves.

Current `PrepareAsync` behavior:

1. Reject an empty upload (`contentLength <= 0`, and again when the bounded read returns no bytes).
2. Enforce `ItemImages:MaxUploadBytes` on the declared length and again inside the bounded read.
3. Read the stream with the existing `ReadBoundedAsync` protection.
4. Inspect the encoded bytes. Ignore the file name and the client MIME type.
5. Accept only JPEG, PNG, and WebP.
6. Reject encoded dimensions outside `MaxInputSide` or `MaxInputPixels`.
7. Reject animated or multi-frame input.
8. Decode exactly one image.
9. Apply EXIF orientation before metadata removal.
10. Clear EXIF, XMP, IPTC, and ICC.
11. Resize proportionally to `MaxDimension` using ImageSharp `ResizeMode.Max` behavior.
12. Never upscale.
13. Encode as WebP.
14. Use `ItemImages:WebpQuality`.
15. Enforce `MaxProcessedBytes`.
16. Return a managed `byte[]`, final width, final height, and `image/webp`.

Exact user-facing validation messages:

```text
The selected file is empty.
Image exceeds the {n} MB upload limit.
Only JPG, PNG, or WebP images are allowed.
Animated or multi-frame images are not supported.
The selected file is not a valid image.
Image dimensions are too large.
The processed image is too large.
```

`{n}` is `_options.MaxUploadBytes / (1024 * 1024)`, which is `8` at the default. Keep that formatting.

Current options and defaults in `ErpWeb.Core/Inventory/ItemImageStorageOptions.cs`:

```text
RootPath               = App_Data/item-images
MaxUploadBytes         = 8 MB
MaxInputPixels         = 25,000,000
MaxInputSide           = 12,000
MaxDimension           = 1024
MaxProcessedBytes      = 4 MB
WebpQuality            = 85
MaxImagesPerItem       = 6
MaxPendingGalleryBytes = 12 MB
```

`WebpQuality` is validated as 1 through 100. Do not hard-code 85.

Managed file shape:

```text
App_Data/item-images/<hashed-company>/<hashed-item>/<guid>.webp
```

No database migration is required.

Existing tests cover supported formats, extension/MIME spoofing with non-image bytes, GIF, SVG, upload size, pixel and side limits, landscape resize to 1024x512, no upscale of 80x40, EXIF Software-tag stripping for orientation 1, managed paths, traversal, cleanup, company scope, and access rights. They do not yet prove orientations 2–8, animated WebP, truncation, semi-transparent alpha, or quality plumbing. Add that coverage. Do not delete the storage and security tests.

---

# 2. TARGET DEPENDENCY

In `ErpWeb.Core/ErpWeb.Core.csproj`, replace:

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="4.1.2" />
```

with:

```xml
<PackageReference Include="SkiaSharp" Version="4.153.1" />
```

`SkiaSharp 4.153.1` is a stable MIT release with an explicit `net10.0` target. For `net10.0` it depends on `SkiaSharp.NativeAssets.Win32`. Leave that package transitive unless the Release `win-x64` publish is missing `libSkiaSharp.dll`.

Do not add:

- preview or RC Skia packages
- `SkiaSharp.Views.*`
- `SkiaSharp.HarfBuzz`
- MAUI, WPF, or WinForms integration packages
- GPU APIs
- a direct `SkiaSharp.NativeAssets.Win32` package, unless publish evidence requires it

Skia types stay private to the image service. Do not expose `SKData`, `SKCodec`, `SKBitmap`, `SKImage`, `SKSurface`, or `SKPixmap` from interfaces, DTOs, entities, or UI.

---

# 3. NON-GOALS

Do not change:

- `IvStockMasterImage` schema or item-image database records
- existing `.webp` files on disk
- path hashing, file naming, or storage-root semantics
- authorization, tenant isolation, or path-traversal protection
- temp-file and atomic-write behavior
- gallery ordering or maximum image count
- UI, API, or service signatures
- cleanup behavior or other Item Master business logic
- `ItemImageStorageOptions` names or defaults
- the ClosedXML / `SixLabors.Fonts` dependency

Do not create a general media framework.

---

# PHASE 0 — SOURCE PIN, BASELINE, AND AUDIT

## 0.1 Verify source

Before coding:

```bash
git status
git rev-parse HEAD
```

The reviewed pin is:

```text
7afdd5c4058b506fa0978e98943b7d2b70639e20
```

If HEAD differs, inspect every image-service, package, and test change before editing. Record the implementation HEAD.

## 0.2 Repository-wide dependency audit

Search for:

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

Known compiled locations on the pin:

```text
ErpWeb.Core/ErpWeb.Core.csproj
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs
```

Markdown plans and docs also mention ImageSharp. Those are not compiled use. `SixLabors.Fonts` comes from ClosedXML. Do not remove it. If HEAD changed, do not assume this list is complete.

## 0.3 Baseline build and tests

```bash
dotnet build ErpWeb.Core/ErpWeb.Core.csproj -c Release
dotnet build ErpWeb/ErpWeb.csproj -c Release
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~IvStockMasterImageServiceTests"
```

Record baseline failures before edits.

## 0.4 Package security baseline

```bash
dotnet list ErpWeb/ErpWeb.csproj package --include-transitive
dotnet list ErpWeb/ErpWeb.csproj package --vulnerable --include-transitive
```

Record the resolved `SixLabors.Fonts` version from that output. Do not copy a version from an old `deps.json` or from this plan.

### Phase 0 gate

PASS only when the source pin is checked, compiled ImageSharp usages are listed, baseline image tests and Release builds are recorded, and the vulnerability baseline plus the resolved Fonts version are recorded.

---

# PHASE 1 — PACKAGE REPLACEMENT

In `ErpWeb.Core/ErpWeb.Core.csproj`, remove the ImageSharp `PackageReference` and add `SkiaSharp` 4.153.1. Restore and build immediately.

### Phase 1 gate

PASS only when Core restores and builds, and no extra Skia packages were added.

---

# PHASE 2 — SAFE ENCODED-CONTENT INSPECTION

This phase replaces ImageSharp `IdentifyAsync`.

## 2.1 Keep the bounded read

Keep `ReadBoundedAsync` and `MaxUploadBytes`. Do not pass the original request stream to Skia.

## 2.2 Byte-backed codec input

```csharp
using var encodedData = SKData.CreateCopy(source);
using var codec = SKCodec.Create(encodedData);
```

`Create(SKData)` returns null when the bytes are not a recognized image. A null codec is `The selected file is not a valid image.` Do not let a null codec throw a `NullReferenceException` out of `PrepareAsync`.

`CreateCopy` owns an independent copy of the bounded upload. Do not create an unmanaged view over temporary managed memory.

## 2.3 Format

Allow only:

```text
SKEncodedImageFormat.Jpeg
SKEncodedImageFormat.Png
SKEncodedImageFormat.Webp
```

Any other `EncodedFormat` is:

```text
Only JPG, PNG, or WebP images are allowed.
```

Ignore the file extension, the client MIME type, and `contentType`. The existing random-bytes test names the payload `renamed.jpg` with `image/jpeg` and expects the message to contain `valid image`. That path is the null-codec path, not the unsupported-raster path.

## 2.4 Header limits before pixel allocation

Using codec info, before `GetPixels`:

```text
width > 0
height > 0
width  <= MaxInputSide
height <= MaxInputSide
(long)width * height <= MaxInputPixels
```

Cast to `long` before multiplying. Failure message:

```text
Image dimensions are too large.
```

### Phase 2 gate

Tests prove JPEG, PNG, and WebP are accepted; a fake extension and a fake MIME type on non-image bytes are rejected as an invalid image; a real GIF and any other recognized non-allowed raster are rejected as unsupported; SVG and random bytes are rejected; side and pixel limits fail before full decode.

---

# PHASE 3 — ANIMATED / MULTI-FRAME REJECTION

Preserve rejection of multi-frame input. The message is:

```text
Animated or multi-frame images are not supported.
```

## 3.1 Detection

Do not treat `FrameCount == 1` as the still-image test. Skia documents that a still image may report `FrameCount == 0` and an empty `FrameInfo`.

```csharp
var isMultiFrame = codec.FrameCount > 1 || codec.FrameInfo.Length > 1;
```

Reject when `isMultiFrame` is true. Do not keep frame zero.

`RepetitionCount != 0` is not sufficient. An animation that plays once can have a repetition count of zero.

Reading frame info may consume the codec stream. Decode from the same `SKData` afterward. If that decode returns `CouldNotRewind` for an otherwise valid still image, create a second `SKCodec` from the same `SKData` and decode with that codec. A second codec is the rewind strategy. It is not a reason to reject a normal JPEG.

## 3.2 Fixtures

Add a real two-frame animated WebP, preferably:

```text
ErpWeb.Tests/Fixtures/Images/animated-2frame.webp
```

or embedded fixed bytes. Do not generate that fixture at test runtime with the production encoder.

Also include one real APNG. Inspect `FrameCount` and `FrameInfo` with the pinned package. Reject it when Skia exposes multiple frames. When the codec treats it as a single frame, record that observed behavior in the final report. Animated WebP remains the mandatory acceptance test.

### Phase 3 gate

PASS only when the real animated WebP is rejected with the exact animated-image message, and the APNG probe result is recorded. A mocked frame count is not proof.

---

# PHASE 4 — STRICT SINGLE-FRAME DECODE

## 4.1 Decode

Decode with `codec.GetPixels(...)` into owned `SKBitmap` memory. The destination info is unpremultiplied 8-bit RGBA:

```csharp
var info = new SKImageInfo(
    codec.Info.Width,
    codec.Info.Height,
    SKColorType.Rgba8888,
    SKAlphaType.Unpremul,
    colorSpace: null);
```

Use the pinned constructor that matches this shape. Do not pass `codec.Info.ColorSpace`. A null color space keeps the source ICC profile out of the decoded bitmap and out of the encoded WebP. Phase 8 repeats that rule for the normalized output bitmap.

Accept only `SKCodecResult.Success`.

Reject all of these, and any other non-success result:

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

Map those failures to `The selected file is not a valid image.` Do not store pixels from a non-success result. If `GetPixels` is asked to scale, it can return `InvalidScale`; decode at `codec.Info` size and resize in Phase 6.

`SKAlphaType.Unpremul` is the decode and intermediate representation. Do not draw an Unpremul bitmap with `SKCanvas`. Orientation in Phase 5 chooses one of the two safe paths.

## 4.2 Cancellation

Skia decode is synchronous. Call `cancellationToken.ThrowIfCancellationRequested()` immediately before and after decode. Do not wrap codec work in `Task.Run`. Do not claim that a native call can be cancelled mid-execution.

## 4.3 Truncation fixtures

Some shortened JPEGs still return `Success` because enough scan data remains. Production code rejects non-success results. It does not reject a buffer that Skia fully decodes as `Success` only because a test called the buffer truncated.

For each truncation case, record:

```text
fixture
bytes removed
SKCodecResult
```

The rejection test uses a deterministic fixture whose pinned Skia build returns a non-success result. Cover JPEG, PNG, and WebP with fixtures that actually fail.

### Phase 4 gate

PASS when normal JPEG, PNG, and WebP inputs decode, the recorded non-success truncation fixtures fail and are not stored, and cancellation requested before decode throws `OperationCanceledException`.

---

# PHASE 5 — EXIF ORIENTATION NORMALIZATION

Current behavior is `image.Mutate(x => x.AutoOrient())` before metadata is cleared. Preserve the pixel result for all 8 origins.

## 5.1 Encoded origin

Read `codec.EncodedOrigin`. Handle every value:

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

Origins 5–8 swap width and height. Orient first, then measure the resize. Do not implement only 1, 3, 6, and 8.

## 5.2 Choose one implementation

Unpremul pixels must not be an `SKCanvas` target. Pick one approach and name it in the final report.

**Direct pixel remapping.** Copy the Unpremul RGBA buffer into a new Unpremul buffer using the 8 origin mappings. This is the preferred path because corner-pixel tests are deterministic.

**Premul canvas.** Draw into a Premul destination bitmap, apply the transform with `SKCanvas`, then continue to resize and encode. Do not point `SKCanvas` at the Unpremul decode bitmap.

Do not mix the two in one request.

## 5.3 Order

```text
inspect
→ decode
→ normalize encoded orientation
→ derive normalized dimensions
→ resize if required
→ encode lossy WebP
```

## 5.4 Fixtures

Use committed or embedded JPEG bytes for orientations 1 through 8. Do not keep ImageSharp in the test project to manufacture them. Assert dimensions and known corner pixel positions. After processing, the pixels are already normalized, so the output must display correctly without the source orientation tag.

### Phase 5 gate

PASS only when all eight origins are proven with corner pixels, or the report shows with evidence that a particular origin cannot occur for the accepted codecs.

---

# PHASE 6 — RESIZE WITHOUT UPSCALING

Preserve ImageSharp `ResizeMode.Max` on the oriented dimensions.

```text
scale = min(1, MaxDimension / longestSide)
longest output side <= MaxDimension
both output sides >= 1
```

Examples:

```text
2048x1024 -> 1024x512
1024x2048 -> 512x1024
800x600   -> 800x600
80x40     -> 80x40
```

Guard integer rounding so a side cannot become 0 or `MaxDimension + 1`.

Use the pinned high-quality CPU API, `SKSamplingOptions` with `SKBitmap.Resize` or `ScalePixels`. Do not use GPU APIs or obsolete quality enums when `SKSamplingOptions` exists. Skip the resize call when both oriented sides are already within `MaxDimension`.

Call `cancellationToken.ThrowIfCancellationRequested()` immediately before and after resize.

### Phase 6 gate

Tests cover landscape, portrait, square, no-upscale, a tiny image, a transparent image, and an oriented image that is then resized.

---

# PHASE 7 — LOSSY WEBP ENCODE

Every accepted input is stored as:

```text
ContentType = image/webp
Extension   = .webp
```

Production encode:

```csharp
var webpOptions = new SKWebpEncoderOptions(
    SKWebpEncoderCompression.Lossy,
    _options.WebpQuality);
using var encoded = SKWebpEncoder.Encode(pixmap, webpOptions);
```

Use the overload the pinned package actually provides (`SKPixmap` or `SKBitmap`). Quality is the configured `WebpQuality` value in the range 1–100. Do not hard-code 85.

Do not call `SKImage.Encode(SKEncodedImageFormat.Webp, quality)` or `SKBitmap.Encode(SKEncodedImageFormat.Webp, quality)` for this output. At quality 100 those APIs select lossless WebP.

## 7.1 Alpha

The alpha fixture has these source pixels:

```text
opaque red
50% alpha green
fully transparent blue
opaque white
```

After encoding and decoding the WebP:

- opaque pixels stay opaque
- the transparent pixel stays transparent
- the semi-transparent pixel stays meaningfully semi-transparent

Do not assert exact lossy RGB values unless the fixture and quality make those values stable. Alpha is the required assertion.

## 7.2 Processed size

```text
output length > 0
output length <= MaxProcessedBytes
```

Otherwise return `The processed image is too large.`

## 7.3 Quality

Assert that the configured quality is passed into the lossy encoder options. Use a nontrivial fixture to show that two configured quality values both produce valid WebP. Assert a byte-size relationship only when it is stable on the pinned package. Do not require quality 100 to be a specific file size.

### Phase 7 gate

PASS when output is WebP, dimensions match, the alpha fixture behaves as above, quality is configuration-driven and lossy, and `MaxProcessedBytes` is enforced.

---

# PHASE 8 — METADATA AND COLOR SPACE

The ImageSharp path clears EXIF, XMP, IPTC, and ICC, then encodes a new WebP. Preserve that privacy result.

```text
decode pixels
→ normalize orientation on those pixels
→ encode a new lossy WebP from the pixels
```

Do not copy source metadata chunks into the output.

When creating the normalized bitmap, do not carry `codec.Info.ColorSpace` forward. Use an explicit null color space on the output image info:

```csharp
new SKImageInfo(width, height, SKColorType.Rgba8888, alphaType, colorSpace: null)
```

or the equivalent safe API for the orientation path chosen in Phase 5. Source ICC must not become an output ICC profile.

The decode working space in Phase 4 may be sRGB so pixel values are defined. That working space is not written into the file. If the lossy encoder still emits an `ICCP` chunk, select the output representation that suppresses it and prove the chunk is absent.

## 8.1 WebP chunk check

Add a small test-only RIFF/WebP chunk reader. Assert the output contains none of:

```text
EXIF
XMP 
ICCP
```

`XMP ` includes the trailing space. Do not add another image library to inspect metadata.

An orientation fixture must show correct pixels and must show that the source EXIF orientation tag is not required to display the output.

### Phase 8 gate

PASS only when source EXIF, XMP, and ICC are demonstrably absent from the output and the source color profile was not inherited.

---

# PHASE 9 — ERROR MAPPING, CANCELLATION, AND DISPOSAL

## 9.1 Cancellation

`OperationCanceledException` propagates. Check the token between:

```text
bounded read
codec inspection
decode
orientation
resize
encode
```

Native Skia calls are synchronous. Cancellation is at those boundaries.

## 9.2 Messages

Return only the existing messages from section 1. Do not return native pointers, server paths, Skia internal errors, or stack traces. Log unexpected failures. Do not catch `OutOfMemoryException`, `StackOverflowException`, or `AccessViolationException` as ordinary validation errors.

## 9.3 Disposal

Dispose every native object the implementation creates:

```text
SKData
SKCodec
SKBitmap
SKImage
SKSurface
SKPixmap
encoded SKData
```

Do not double-dispose a shared native object. Do not return a view whose owner is already disposed. The service return value remains a managed `byte[]`.

### Phase 9 gate

A repeated prepare loop completes without a native-handle or disposal failure. A cancellation requested before decode surfaces as `OperationCanceledException`.

---

# PHASE 10 — TEST MIGRATION

Update `ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs`. Remove every `SixLabors.ImageSharp` using. The test project must not keep ImageSharp to build fixtures.

## 10.1 Generated fixtures

SkiaSharp may generate ordinary JPEG, PNG, and WebP bytes for happy-path tests.

## 10.2 Fixed fixtures

Use committed or embedded bytes, independent of the production encoder, for:

```text
EXIF orientation 1..8
animated WebP
truncated inputs that return a non-success SKCodecResult
metadata-bearing JPEG
one APNG probe
```

## Regression matrix

Input:

- JPEG, PNG, and WebP accepted
- extension spoof and MIME spoof on non-image bytes rejected as an invalid image
- random bytes rejected
- truncated JPEG, PNG, and WebP fixtures that return non-success rejected, with fixture, bytes removed, and `SKCodecResult` recorded
- GIF rejected as unsupported
- SVG rejected
- real animated WebP rejected with the animated-image message
- APNG result recorded
- empty upload rejected
- `MaxUploadBytes`, `MaxInputSide`, and `MaxInputPixels` enforced, the last two before full decode

Processing:

- all 8 EXIF origins, with dimensions and corner pixels
- landscape, portrait, square, no-upscale, and aspect ratio
- alpha fixture: opaque red, 50% alpha green, fully transparent blue, opaque white
- WebP output
- configured quality passed to the lossy encoder
- `MaxProcessedBytes`
- no `EXIF`, `XMP `, or `ICCP` chunk in the output

Keep unchanged:

- managed relative path and hash path
- traversal and absolute-path rejection
- idempotent cleanup
- no orphan temp file after a store failure
- company isolation
- access-right enforcement

---

# PHASE 11 — REPOSITORY CLEANUP

```bash
git grep -n "SixLabors"
git grep -n "ImageSharp"
```

No compiled product or test use of ImageSharp may remain. Hits in historical plans and docs are not a failure. `SixLabors.Fonts` remaining as a ClosedXML transitive dependency is not a failure.

Then:

```bash
dotnet list ErpWeb.Core/ErpWeb.Core.csproj package
dotnet list ErpWeb/ErpWeb.csproj package --include-transitive
dotnet list ErpWeb/ErpWeb.csproj package --vulnerable --include-transitive
```

Required:

- no direct `SixLabors.ImageSharp`
- `SkiaSharp` resolved at 4.153.1
- no unexpected vulnerable package introduced by this migration
- the resolved `SixLabors.Fonts` version recorded from this command
- any unrelated transitive ImageSharp reference reported explicitly

---

# PHASE 12 — THIRD-PARTY LICENSE NOTICES

This migration avoids the Six Labors commercial license. SkiaSharp is MIT, and Skia uses BSD-style upstream notices. Those notices still apply.

Apply the first matching rule:

1. If the repository already has a third-party disclosure mechanism, record SkiaSharp and Skia there.
2. If ERP binaries are distributed to customers and no mechanism exists, add `THIRD-PARTY-NOTICES.txt` (or the repository's equivalent) containing the required notices.
3. If the organization's deployment process already supplies these notices outside the repository, record that evidence in the final report instead of adding a file.

Minimum record:

```text
SkiaSharp — MIT
Skia — applicable BSD-style upstream notices
```

Do not write that there are no license obligations.

On the reviewed pin there is no `THIRD-PARTY-NOTICES` file. Re-check before choosing rule 2 or rule 3.

---

# PHASE 13 — WINDOWS SERVER / IIS PUBLISH PROOF

Unit tests do not prove native loading.

Publish the host the way the ERP is published, and also run:

```bash
dotnet publish ErpWeb/ErpWeb.csproj -c Release -r win-x64 --self-contained false
```

Do not change the production deployment model to make this command pass.

Confirm the publish output contains `libSkiaSharp.dll` in the native output layout. Confirm there is no `DllNotFoundException` and no `BadImageFormatException`, that the IIS app-pool bitness matches x64, and that the application starts. Fix a missing native asset through the package reference or RID. Do not download a DLL by hand.

IIS smoke, in order:

1. Upload JPEG.
2. Upload PNG.
3. Upload WebP.
4. Upload a phone or camera JPEG with EXIF orientation.
5. Upload an image larger than 1024 on the long side.
6. Upload a small image.
7. Reopen the image.
8. Replace the image.
9. Delete the image.
10. Restart the app pool.
11. Reopen a WebP that was stored before this migration.

### Phase 13 gate

PASS only when the Release x64 publish loads the native asset and the IIS image workflow succeeds, including previously stored WebP files.

---

# PHASE 14 — PERFORMANCE / MEMORY SMOKE

Run, and record input bytes, input dimensions, output dimensions, output bytes, and prepare time:

```text
80x40
typical phone photo
2048x1024
near MaxInputPixels
near MaxUploadBytes
corrupt input
animated input
```

This is a smoke record, not a benchmark project.

Security invariants that must still hold:

- byte limit before image decode
- dimension and pixel limits before full decode
- no unbounded image allocation
- no filename-derived filesystem path
- no source metadata propagation

---

# PHASE 15 — ACCEPTANCE CHECKLIST

- [ ] Source HEAD recorded.
- [ ] Direct `SixLabors.ImageSharp` reference removed.
- [ ] No compiled ImageSharp usage remains in product or test code.
- [ ] `SixLabors.Fonts` left in place, with the resolved version recorded from `dotnet list`.
- [ ] `SkiaSharp` 4.153.1, with no preview or RC package.
- [ ] No direct native-assets package unless publish required it.
- [ ] Core Release build passes.
- [ ] ERP host Release build passes.
- [ ] Image-service tests pass.
- [ ] JPEG, PNG, and WebP detection passes.
- [ ] Extension and MIME spoofing of non-image bytes still returns the invalid-image message.
- [ ] A null codec returns `The selected file is not a valid image.`
- [ ] A recognized unsupported raster returns `Only JPG, PNG, or WebP images are allowed.`
- [ ] GIF and SVG are rejected.
- [ ] A real animated WebP is rejected with `Animated or multi-frame images are not supported.`
- [ ] Still-image `FrameCount == 0` is accepted.
- [ ] APNG probe result is recorded.
- [ ] Truncation fixtures that return non-success are rejected and the result code is recorded.
- [ ] A partial decode is never stored.
- [ ] `MaxUploadBytes` is enforced.
- [ ] `MaxInputSide` and `MaxInputPixels` are enforced before full decode.
- [ ] Decode accepts only `SKCodecResult.Success`.
- [ ] Unpremul is not used as an `SKCanvas` target. The orientation approach is named.
- [ ] All 8 EXIF origins pass, including corner pixels.
- [ ] No-upscale and aspect ratio are preserved.
- [ ] WebP quality is configuration-driven.
- [ ] The encoder is explicit lossy `SKWebpEncoder`.
- [ ] Semi-transparent alpha is preserved.
- [ ] `MaxProcessedBytes` is enforced.
- [ ] Source EXIF, XMP, and ICC are absent from the output.
- [ ] Source color profile is not inherited.
- [ ] Cancellation propagates at stage boundaries.
- [ ] Native Skia objects are disposed.
- [ ] Storage, path, security, and cleanup tests stay green.
- [ ] Package vulnerability results are reviewed.
- [ ] The third-party notice mechanism is recorded.
- [ ] Windows x64 publish contains a working native asset.
- [ ] IIS smoke test passes.
- [ ] Existing stored WebP files still open.
- [ ] No database migration.
- [ ] No unrelated Item Master or UI behavior changed.

---

# 16. IMPLEMENTATION SHAPE

Keep the change inside `IvStockMasterImageService` with private helpers.

```text
PrepareAsync
    |
    +-- ReadBoundedAsync
    |
    +-- SKData.CreateCopy
    |
    +-- SKCodec.Create
    |      null -> invalid image
    |
    +-- TryInspectImage
    |      format, encoded size, frame metadata, encoded origin
    |
    +-- DecodeSingleFrameStrict
    |      Rgba8888 / Unpremul
    |      require SKCodecResult.Success
    |
    +-- NormalizeOrientation
    |      direct Unpremul remap, or Premul SKCanvas
    |
    +-- CalculateTargetSize
    |
    +-- ResizeIfRequired
    |
    +-- EncodeLossyWebp
    |      SKWebpEncoder + SKWebpEncoderCompression.Lossy
    |      color space not copied from the source
    |
    +-- MaxProcessedBytes
    |
    +-- IvPreparedStockImage (managed byte[])
```

Reasonable private helpers:

```text
TryInspectImage
IsAllowedEncodedFormat
DecodeSingleFrameStrict
NormalizeOrientation
CalculateTargetSize
ResizeIfRequired
EncodeLossyWebp
```

Do not add a second public image service.

---

# 17. FILES

Primary:

```text
ErpWeb.Core/ErpWeb.Core.csproj
ErpWeb.Core/Inventory/IvStockMasterImageService.cs
ErpWeb.Tests/Inventory/Master/IvStockMasterImageServiceTests.cs
```

Fixtures:

```text
ErpWeb.Tests/Fixtures/Images/*
```

License file only when Phase 12 rule 2 applies:

```text
THIRD-PARTY-NOTICES.txt
```

Normally unchanged:

```text
ErpWeb.Core/Inventory/ItemImageStorageOptions.cs
database entities
database scripts
UI files
```

If the change needs to leave this boundary, stop and explain why.

---

# 18. DO NOT

- use `System.Drawing.Common`
- trust the extension or the client MIME type
- remove the bounded upload read
- full-decode before the dimension checks
- accept a non-success or partial decode
- treat `FrameCount != 1` as the animation test
- use `RepetitionCount` alone
- keep frame zero of an animated image
- skip mirrored EXIF origins
- resize before orientation
- upscale
- change the stored format away from WebP
- hard-code quality
- call `Encode(SKEncodedImageFormat.Webp, quality)` for production output
- use an Unpremul bitmap as an `SKCanvas` target
- copy the source codec color space into the output
- copy source EXIF, XMP, or ICC
- add a metadata library for the tests
- use GPU rendering
- wrap Skia work in `Task.Run`
- leak Skia native objects
- change storage paths
- rewrite database, gallery, or UI logic
- keep ImageSharp in tests
- remove `SixLabors.Fonts`
- hard-code a Fonts version
- add preview or RC Skia packages
- deploy a manually downloaded native DLL
- skip the IIS check
- claim there are no license obligations

---

# 19. FINAL REPORT

Report these items. Compilation by itself is not READY.

1. **Source HEAD implemented against**
2. **Files changed**
3. **Package changes**, including the resolved `SixLabors.Fonts` version from `dotnet list` and confirmation it was left in place
4. **ImageSharp direct references remaining**
5. **ImageSharp transitive references remaining**
6. **SkiaSharp and native versions resolved**
7. **Behavior preserved**, including the exact validation messages
8. **Strict decode / result handling**, including truncation fixture, bytes removed, and `SKCodecResult`
9. **Animated-image detection**, the real WebP fixture result, the APNG probe, and how `FrameCount == 0` was handled
10. **EXIF orientation coverage** for all 8 origins, plus which orientation implementation was used (direct remap or Premul canvas)
11. **Metadata and color-space proof**, including absence of `EXIF`, `XMP `, and `ICCP`
12. **WebP output proof**: lossy `SKWebpEncoder`, config-driven quality, and the semi-transparent alpha fixture
13. **Cancellation and disposal proof**
14. **Focused test result**
15. **Broader affected test result**
16. **Release build result**
17. **Package vulnerability result**
18. **License-notice result**, including which Phase 12 rule was applied
19. **Windows x64 publish and native asset result**
20. **IIS smoke-test result**
21. **Known output differences**, including any truncation that still returns `Success` and any APNG the codec treats as one frame
22. **FINAL RESULT: READY / NOT READY**

READY requires the focused tests, Release build, vulnerability review, license-notice review, Windows x64 publish, and IIS smoke test.

---

# 20. APPROVAL

**STATUS: APPROVED FOR IMPLEMENTATION**

The pin uses ImageSharp only for Item Master upload normalization. Replacing that path with SkiaSharp 4.153.1 is appropriate when the rules in this document are followed.

This approval is for implementation. Production deployment waits until the Phase 15 checklist is complete, including publish and IIS evidence.

---

# 21. VERIFIED EXTERNAL FACTS

Reviewed on 2026-10-26:

- `SkiaSharp 4.153.1` is a stable MIT release with an explicit `net10.0` target and a dependency on `SkiaSharp.NativeAssets.Win32`.
- `SKCodec.Create(SKData)` returns null when the data is not a recognized image.
- `SKCodec` exposes `EncodedFormat`, `EncodedOrigin`, `FrameCount`, `FrameInfo`, and `RepetitionCount`.
- Still images may report `FrameCount == 0`.
- `SKCodec.GetPixels` returns `SKCodecResult`, including `Success`, `IncompleteInput`, `ErrorInInput`, and `InvalidInput`.
- `SKImage.Encode(SKEncodedImageFormat.Webp, 100)` follows Skia's rule that WebP quality 100 is lossless. ImageSharp `WebpEncoder` at quality 100 stays lossy. Production code therefore uses `SKWebpEncoder` with `SKWebpEncoderCompression.Lossy`.
- `SKWebpEncoderOptions` takes a compression mode and a quality value.
- `SKBitmap.Resize` / `ScalePixels` support `SKSamplingOptions`.

References:

- https://www.nuget.org/packages/SkiaSharp/4.153.1
- https://github.com/mono/SkiaSharp/releases
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec.framecount
- https://learn.microsoft.com/dotnet/api/skiasharp.skcodec.getpixels
- https://learn.microsoft.com/dotnet/api/skiasharp.skwebpencoderoptions
- https://learn.microsoft.com/dotnet/api/skiasharp.skbitmap.resize
