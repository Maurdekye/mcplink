# Resonite Rendering & Assets

> Resonite assets, rendering, materials, mesh and audio reference (ILSpy-verified) — the decoupled Renderite/Awwdio split, asset load lifecycle, importing, texture/color encoding, MeshX, procedural mesh/asset generation, material families, MeshRenderer/SkinnedMeshRenderer, Light/Camera, audio.

## Assets, rendering, materials, mesh & audio

Resonite runs a **decoupled out-of-process renderer ("Renderite")**: many render-component sync members
are typed against enums/types in **`Renderite.Shared.dll`** (also `Renderite.Host.dll`), NOT FrooxEngine
— e.g. `MeshRenderer.ShadowCastMode`, `Light.LightType`, `Camera.Projection`,
`StaticTexture2D.WrapModeU/V` (`Renderite.Shared.TextureWrapMode`), `ProceduralTexture.Format`
(`Renderite.Shared.TextureFormat`). Look these up in `Renderite.Shared.dll`. Audio runs on a separate
**`Awwdio`** engine (`Awwdio.dll`).

### Asset lifecycle & loading
- `Asset.LoadState : AssetLoadState` cycles `Created → LoadStarted → PartiallyLoaded → FullyLoaded`
  with terminals `Failed` / `Unloaded` (`AssetLoadState` enum, driven by `Asset.SetLoadState`).
  `FullyLoaded` is the success terminal; `PartiallyLoaded` = a lower-quality variant is up. **Only
  `AssetVariantType` kinds (Texture, Cubemap, Volume, Mesh, Shader, GaussianSplat) get progressive
  variants**, so audio/animation/font/document have no `PartiallyLoaded` phase.
- **Asset data is lock-gated, not free-threaded.** Mutating backing data (Bitmap2D/MeshX behind a Static
  provider) needs the write lock: `Asset.RequestRead/RequestWrite` (Action/Task) or
  `RequestReadLock/RequestWriteLock(lockObj)` + `Release*` (internal SpinLock + read/write lock queue).
  The `Static*` edit helpers wrap this. Asset also exposes `Version`, `ActiveRequestCount`,
  `UnloadDelay`, `DownloadProgress`, `HighPriorityIntegration`.
- **Refcount-driven unload.** `AssetRef<T>` caches the resolved `Asset` (+ `IsAssetAvailable`,
  `ListenToAssetUpdates`, fires `AssetUpdated`); `AssetProvider<A>` tracks consumers in `references` /
  `updateListeners` sets (`AssetReferenceCount` public). An asset stays loaded only while referenced
  (`TryFreeAsset`/`FreeAsset` at refcount 0), modulated by `AlwaysLoad`/`ForceUnload`/`UnloadDelay`.
- `AssetManager.Initialize` seeds reusable defaults: `WhiteTexture`, `BlackTexture`, `ClearTexture`,
  procedural 128² `DarkCheckerTexture`, 64px `DarkCheckerCubemap`. Variant-baking thread pools scale
  with CPU: texture generator `maxThreads = max(1, PhysicalProcessorCount - 2)`, metadata/general
  `count/4` — so texture/mesh variant generation is parallel.

### Importing — `UniversalImporter` + settings
- `Elements.Assets.AssetClass` enumerates importable categories: Text, Package, Object, Texture, Cubemap,
  Volume, Document, Model, **PointCloud, GaussianSplat**, Audio, Video, Shader, Animation, Font, Folder,
  Subtitle, Special. `UniversalImporter.Import(AssetClass, files, …)` dispatches by it
  (`SpawnGaussianSplat` exists).
- **`ModelImportSettings`** factory presets `Unlit()/XiexeToon()/Wireframe()/PBS(…, preferSpecular)`;
  fields incl. `MaxTextureSize`, `Scale/Center/Ground/Rescale`, `Winding`, `TextureConversion`
  (`{Auto,PNG,WEBP,JPEG}`), `GenerateColliders`, `GenerateSkeletonBones/SetupIK/ForceTpose`,
  `ImportBones/Animations/VertexColors/Emissive/Lights`, `DeduplicateInstances`, `SplitSubmeshes`,
  `OptimizeModel`, `MakeDualSided/MakeFlatShaded`, `ForcePointFiltering`, `ForceNoMipmaps`,
  `ForceCompression`/`ForceBlendMode` (nullable), `MaterialMapper`.

### Texture & color encoding (Elements.Assets)
- `TextureCompression` (23 formats): Raw RGBA, **RawRGBAHalf** + **BC6H** (HDR/half-float), BC1/3/4/7
  (Crunched, LZMA, perceptual & non-perceptual), `BC3nm` (normal-map), ETC2 RGB/RGBA8, ASTC 4×4…12×12.
  `MeshCompression = {None, LZ4, LZMA}`.
- `Filtering = {Bilinear, Box, Lanczos3}` (mipmap gen / `Rescale` — **no point/nearest**; point is the
  separate `ForcePointFiltering` import bool). `WrapMode = {Clamp, Repeat}` (pipeline) — distinct from
  the `Renderite.Shared.TextureWrapMode` on the actual `StaticTexture2D` fields.
- HDR-in-LDR packing: `ColorPreprocess = {None, sRGB, HDRsRGB, LogLUV, RGBM}`,
  `AlphaPreprocess = {None, sRGB, LogLUV, RGBM}`, `AlphaHandling = {KeepOriginal, ForceRGB, ForceRGBA}`.
  `TextureType = {Albedo, Normal, Height, Emissive, Specular, Gloss, Roughness, AmbientOcclusion,
  UNKNOWN}` tags semantic role.
- **`StaticTexture2D`** `OnAwake` defaults: `MipMaps=true`, `KeepOriginalMipMaps=false`,
  `MipMapFilter=Box`, `CrunchCompressed=true`, `WrapModeU/V=Repeat`, `PowerOfTwoAlignThreshold=0.05`.
  `MinSize/MaxSize` are `Sync<int?>`, **null by default** (global `TextureQualitySettings` cap applies).
  Set `Readable=true` to read pixels back at runtime. In-engine Task ops: `Rescale, Crop, Trim/
  TrimTransparent, FlipH/V, Rotate90/180, MakeSquare, TileLoop/TileMirror, BleedColorToAlpha,
  LuminanceThreshold, KMeansCluster, ToNearestPOT`.

### MeshX (Elements.Assets) — single precision, fixed limits
- Positions/normals `float3`, tangents `float4`, colors linear `color` — **all single-precision.**
- **Exactly 4 UV channels** (`RawUV0s`…`RawUV3s`, `HasUV0s`…), each independently 2D/3D/4D
  (`SetUV_Dimension`/`GetUV_Dimension`/`HasUV_2D/3D/4D`).
- **Max 4 bone influences per vertex**: `BoneBinding` struct = `boneIndex0..3` + `weight0..3`
  (`int4 PackedIndicies` / `float4 PackedWeights`); `SortTrimAndNormalizeBoneWeights(trim=0.0001,…)`
  enforces it. Supports submeshes, bones, blendshapes (optional per-shape normals/tangents).
- `MeshDataType = {Mesh, MeshCollider, DualSidedMeshCollider, ConvexHullCollider}` — one source mesh
  bakes different render-vs-physics variants (`StaticMesh.ConvertToConvexHull/ConvertToPointCloud`,
  `ProceduralMesh.CreateCollider`).

### Procedural assets
- `ProceduralMesh` (abstract): override `UpdateMeshData(MeshX)` / `…Async` + `ClearMeshData()`; base
  manages a `MeshX`, `MeshUploadHint`, `OverrideBoundingBox`/`OverridenBoundingBox`, `Profile`
  (ColorProfile), `BakeMesh()` (→ `StaticMesh`), `SetupRenderer()`, `CreateCollider()`; auto
  `GenerateErrorIndication` on failure. **~50 concrete subtypes**: primitives (Box/Sphere/IcoSphere/
  Cylinder/Cone/Capsule/Torus/Quad/Circle/Ring/Grid/Arrow), tubes (Tube/BezierTube/BentTube), Convex
  Hull, ConstrainedDelaunay, Bevel*, data-viz (LineGraph, AudioSourceWaveform, PointCluster,
  BoneWeightDiagnostic).
- `ProceduralTexture`: `Size : int2`, `Mipmaps`, `Format`, protected `GenerateSize/Mipmaps/Format`.

### Materials
- All `MaterialProvider` subclasses implement `UpdateMaterial(ref MaterialUpdateWriter)`. **PBS set is
  doubled into metallic vs specular workflows**: each family is `<X>` + `<X>Metallic` + `<X>Specular`
  (DualSided, Triplanar, MultiUV, Slice, Rim, Stencil, Intersect, DistanceLerp, Displace, VertexColor,
  ColorMask, ColorSplat, PBSLerp…). `IPBS_Metallic` = `Metallic`+`Smoothness`+`MetallicMap`;
  `IPBS_Specular` = `SpecularColor`+`Smoothness`+`SpecularMap`. Model import picks via
  `ModelImportSettings.PBS(…, preferSpecular)`.
- Other families: `UnlitMaterial`/Overlay/Distance/Volume/Text/UI_* unlit, toon
  (`XiexeToonMaterial`/`FlatLitToonMaterial`), `Fresnel*`, `Matcap`, `Fur`, `Reflection`, `Wireframe`,
  sky (`GradientSkyMaterial`/`ProceduralSkyMaterial : ISkyboxMaterial`), and post/blit (Blur, Gamma,
  Grayscale, HSV, Invert, Pixelate, Posterize, Refract, Threshold, LUT, ChannelMatrix).
- **PBS fields map to Unity Standard shader properties** (static `MaterialProperty` handles `_MainTex`,
  `_Color`, `_EmissionColor/_EmissionMap`, `_BumpMap/_BumpScale`, `_Parallax/_ParallaxMap`,
  `_DetailAlbedoMap/_DetailNormalMap`, `_OcclusionMap`, `_Cutoff`, `_OffsetFactor/_OffsetUnits`).
  `UpdateKeywords` drives AlphaBlend/Premultiply/Cutout keywords.

### MeshRenderer / SkinnedMeshRenderer
- `MeshRenderer.Materials` (`SyncAssetList<Material>`) maps **1:1 to mesh submeshes**;
  `MaterialPropertyBlocks` overrides props without unique materials. **`GetUniqueMaterial(index)`**
  deep-copies a shared material (one whose `References` point elsewhere) onto a new slot under
  `World.AssetsSlot` ("… - Unique Material") so edits don't bleed to other renderers;
  `ReplaceAllMaterials`, `SplitSubmeshes`, `MergeByMaterial` restructure the mapping.
- `MeshRenderer` also has `ShadowCastMode`, `MotionVectorMode` (`{Camera,Object,NoMotion}`),
  `SortingOrder : Sync<int>` (transparency draw order).
- **`SkinnedMeshRenderer` bounds modes** (`SkinnedBounds`, field `BoundsComputeMethod`) trade
  cost/accuracy: `Static, Explicit, Proxy, FastDisjointRootApproximate, MediumPerBoneApproximate,
  SlowRealtimeAccurate` — wrong bounds cause culling pop-in, so this is the knob (`ComputeLocalBounds`).
- Blendshapes: `BlendShapeWeights` (SyncFieldList); `Get/SetBlendShapeWeight`,
  `BlendShapeIndex/Name`, `HasBlendshape`; `MeshBlendshapeCount` vs `RenderableBlendshapeCount` differ.
  Edit ops: `BakeBlendshape, RemoveBlendshape, SplitBlenshapeAlongAxis, SplitBlendshapeIntoStaticMesh,
  StripEmptyBlendshapes, MergeBlendshapes, SortBlendshapesByName, StripEmptyBones`.
  `GetBoneTransforms(span, targetSpace)` → `float4x4[]`.

### Light & Camera
- `Light.OnAwake` defaults: `LightType=Point` (`{Point,Directional,Spot}`, byte-backed),
  `Intensity=1`, `Color=White`, `Range=10`, `SpotAngle=60`, `ShadowStrength=1`, `ShadowNearPlane=0.2`,
  `ShadowMapResolution=0` (auto), `ShadowBias=0.125`, `ShadowNormalBias=0.6`; `ShadowType
  {None,Hard,Soft}`; `Cookie : AssetRef<ITexture>` for projected textures.
- `Camera.OnAwake` defaults: `Projection=Perspective` (`{Perspective,Orthographic,Panoramic}` —
  Panoramic = 360 capture), `FieldOfView=60`, `OrthographicSize=8`, `NearClipping=0.1`,
  `FarClipping=4096`, `Clear=Skybox` (`{Skybox,Color,Depth,Nothing}`), `Postprocessing/MotionBlur/
  RenderShadows=true`. Plus `SelectiveRender`/`ExcludeRender` slot lists (layer-style culling),
  `ScreenSpaceReflections`, `RenderTexture` ref, `UVToRay/PointToUV` projection math.
  **`Camera.RenderToTexture(int2 res, Slot root=null, string format="webp", int quality=200)`** → a
  `StaticTexture2D` (also `RenderToAsset/RenderToBitmap`).

### Audio (Awwdio engine)
- `AudioOutput.OnAwake` defaults: `Volume=1`, `SpatialBlend=1`, `Spatialize=true`, `Pitch=1`,
  `DopplerLevel=1`, `MinDistance=1`, `MaxDistance=500`, `Priority=128`, `AudioTypeGroup=SoundEffect`,
  `DistanceSpace=Local`. Consts `DEFAULT_PRIORITY=128`, `DEFAULT_MIN_DISTANCE=1`,
  `DEFAULT_MAX_DISTANCE=500`, `DEFAULT_ROLLOFF=LogarithmicFadeOff`. `ActualVolume = Clamp01(Volume) ×
  groupVolume × user.LocalVolume`. `SetupAsUI()` → group=UI, doppler=0, ignore effects.
- `AudioRolloffCurve = {LogarithmicInfinite, LogarithmicClamped, LogarithmicFadeOff, Linear,
  Logarithmic}`; `AudioTypeGroup = {SoundEffect, Multimedia, Voice, UI}` (per-group volume via
  `Engine.AudioSystem.GetAudioTypeGroupVolume`); `AudioDistanceSpace = {Local, Global}`.
- **Distance scaling** (`GetActualDistances`): in `Local` space min/max/spatialization distances are
  multiplied by `MathX.AvgComponent(Slot.GlobalScale)` clamped to `[MinScale,MaxScale]` (so audio
  falloff scales with the object); `Global` uses raw values. `+Infinity` max is exempt; if min>max they
  swap. **Legacy worlds with no `DistanceSpace` key load as Global** (`OnLoading`, gated by
  `GetFeatureFlag("Awwdio")`; legacy doppler/reverb-ignore adapters provide back-compat).
- `StaticAudioClip.OnAwake`: `LoadMode=Automatic`, `SampleRateMode=Conform`.
  `AudioLoadMode = {Automatic, StreamFromFile, StreamFromMemory, FullyDecode}` — large clips can stream
  vs fully decode. Re-encode (`ConvertToWAV/Vorbis/FLAC`) and DSP (`Normalize, Denoise, FadeIn/Out,
  TrimSilence, ApplyZitaReverb`).
- `AudioClipPlayerBase`: `MAX_PLAYBACK_SPEED=32` (Speed clamped), `ERROR_FADE_SAMPLES=256`;
  `Play/Stop/Pause/Resume`, `Position/NormalizedPosition`, `Speed`, `Loop`, `IsStreaming`,
  `ChannelCount`, `ClipLength`.

### RadiantUI constants (UIX styling)
`RadiantUI_Constants` nested palettes. Neutrals: `DARK #11151d`, `MID #2b2f35`, `DARKLIGHT #4a4a4d`,
`MIDLIGHT #86888b`, `LIGHT #e1e1e0`, `DISABLED #4d4d4d`. Hero accents: `YELLOW #f8f770`,
`GREEN #59eb5c`, `RED #ff7676`, `PURPLE #ba64f2`, `CYAN #61d1fa`, `ORANGE #e69e50` (+ MidLight/DarkLight/
Sub/Dark tiers). Aliases: `BG_COLOR=Neutrals.DARK`, `TEXT_COLOR=Neutrals.LIGHT`,
`BUTTON_COLOR=Neutrals.MID`, `HIGHLIGHT_COLOR=Sub.PURPLE`, `HEADING_COLOR=Hero.PURPLE`,
`LABEL_COLOR=Hero.YELLOW`, `SLIDER_COLOR=Sub.PURPLE`.
- Tint helpers blend only **10%** toward the accent: `GetTintedButton/Text(tint)=
  LerpUnclamped(base, tint, 0.1f)` (`BLEND_FACTOR=0.1`).
- `SetupButtonStyle`: ButtonColor=`MID`, Circle sprite (FixedSize 16), `DisabledColor=DARK`,
  `DisabledAlpha=0.25`; `SetupBaseStyle` adds `TextColor=LIGHT`, `HighlightColor=Hero.YELLOW`,
  `SliderFillColor=Sub.PURPLE`; `SetupEditorStyle` additionally sets `Style.Font =
  World.GetBolderFont()` and `ButtonTextPadding=2` (4 if extraPadding).

### Procedural asset generation (in-engine, not import)

Building an asset from code/graph means subclassing a `ProceduralAssetProvider`-derived component and filling a CPU-side data object on a background thread, which the engine then uploads to the renderer. The base `FrooxEngine.ProceduralAssetProvider` is an open generic (`A` = the asset, e.g. `Mesh`, `Texture2D`); it owns the generation lifecycle and schedules generation onto the asset thread via `RunBackgroundAssetUpdateAsync()` / `RunBackgroundAssetUpdate()` (`ProceduralAssetProvider.<RunBackgroundAssetUpdateAsync>d__19` state machine). Concrete families: `ProceduralMesh`, `ProceduralTextureBase` / `ProceduralTexture3DBase`, `ProceduralCubemapBase`, `ProceduralAudioClip`, `ProceduralFont`, `ProceduralAnimation`, `ProceduralGaussianSplat` (all expose `UpdateAssetDataAsync(A)`).

- **Mesh path (`FrooxEngine.ProceduralMesh : ProceduralAssetProvider`).** Override **`UpdateMeshData(MeshX)`** (sync) or **`UpdateMeshDataAsync(MeshX)`** (async, preferred for heavy gen); you MUST implement abstract **`ClearMeshData()`**. The engine's sealed `UpdateAssetDataAsync(Mesh)` first calls `PrepareMeshUpdate()` then awaits your `UpdateMeshDataAsync` with `.ConfigureAwait(false)` (`ProceduralMesh.UpdateAssetDataAsync`). `PrepareMeshUpdate()` lazily news the `meshx` field (the `protected MeshX meshx`), sets `meshx.Profile = Profile`, and calls `uploadHint.SetAll()` (`ProceduralMesh.PrepareMeshUpdate`). Your override mutates the shared `meshx` in place — typically `meshx.Clear()` then add geometry. `OnAwake` sets `Profile.Value = ColorProfile.Linear` (`ProceduralMesh.OnAwake`).
  - **Upload** is `ProceduralMesh.UploadAssetData`: reads `OverrideBoundingBox`/`OverridenBoundingBox` (Sync fields) for an optional manual `BoundingBox`, calls `uploadHint.ResetUnusedChannels(meshx)`, forces `uploadHint[MeshUploadHint.Flag.Dynamic]=true` and `[Flag.Readable]=true`, then `Asset.SetFromMeshX(meshx, uploadHint, overrideBounds, integratedCallback)` (`Mesh.SetFromMeshX(MeshX, MeshUploadHint, BoundingBox?, AssetIntegrated)`). So procedural meshes are always uploaded **Dynamic + Readable** (CPU-readable, re-uploadable) — fine to regenerate repeatedly, but not the cheapest GPU residency.
  - **Bake to static**: `BakeMesh()` → `BakeMeshAsync()` force-loads if unreferenced, awaits `Asset.Data` over `NextUpdate`s, takes a read lock, `await default(ToBackground)`, saves bytes via `Engine.LocalDB.SaveAssetAsync`, `await default(ToWorld)`, attaches a `StaticMesh` with that URL and `World.ReplaceReferenceTargets(this, staticMesh)` then `Destroy()`s the provider (`ProceduralMesh.BakeMeshAsync`). This is the canonical async-marshaling idiom: `ToBackground` for I/O, `ToWorld` to mutate slots.
  - **Helpers**: `CreateCollider()` attaches a `MeshCollider` pointing back at `this` (`ProceduralMesh.CreateCollider`); `SetupRenderer<M>()` attaches a `MeshRenderer` + material of type `M` (`ProceduralMesh.SetupRenderer`). `GenerateErrorIndication()` does `meshx.Clear(); new Quad(meshx).Update();` — i.e. a fallback quad when gen fails (`ProceduralMesh.GenerateErrorIndication`).

#### `OverrideBoundingBox` / `OverridenBoundingBox` — culling AABB only, not the real bounds (ILSpy-verified 2026-08-07)

Two `Sync` fields on `ProceduralMesh` (all ~50 subtypes) and `ProceduralGaussianSplat` — note the engine's
spelling, `Overriden`, one `d`. **`StaticMesh` has no such field**; imported meshes carry bounds in
`MeshMetadata` instead.

- **What it actually touches**: only the render-side culling AABB. `UploadAssetData` feeds it through
  `Mesh.SetFromMeshX` → `Mesh.UpdateMeshData` (`overrideBounds ?? Bounds`, also backstopping
  `MeshBufferGenerator.EnsureValidSubmeshes` when there's no `MeshMetadata` — true for every procedural
  mesh, so the override always wins there) → `Renderite.Unity.MeshAsset.Upload` sets Unity's
  `Mesh.bounds` with `MeshUpdateFlags.DontRecalculateBounds`, so nothing recomputes it afterward. That's
  frustum/shadow culling, full stop.
- **What it does NOT touch**: the engine-side `Mesh.Bounds` (`Metadata?.Bounds` else
  `Data.CalculateBoundingBox()`, version-cached) is a completely separate value the override never
  writes — `MeshRenderer.LocalBoundingBox`/`GlobalBoundingBox`, every `IBounded` consumer
  (`BoundingBoxDriver`, gizmos, fit-to-object), colliders/raycasts, and `ComputeExactBounds` all keep
  seeing real geometry regardless. **`SkinnedMeshRenderer` ignores this field entirely** — its bounds
  come from `BoundsComputeMethod`/`ExplicitLocalBounds`/`ProxyBoundsSource` instead.
- **When to reach for it**: vertex-shader displacement (outline/wind/growth shaders push verts outside
  the source AABB → early cull-while-visible pop-out — enlarge the box); shrinking an oversized box to
  cull earlier; skipping `Data.CalculateBoundingBox()` on every regen of a per-frame procedural mesh
  (`BoneMetadata`/`ApproximateBoneBounds` still evaluate regardless).
- **Footguns**: `default(BoundingBox)` is a **degenerate point** (`min=max=(0,0,0)`, min/max not
  centre/size) — ticking the bool without filling the box culls the mesh the instant its local origin
  leaves frustum. Values are **mesh-local space**. NaN/Inf `Size` → one-shot
  `UniLog.Warning` (`Mesh._invalidBoundsWarned`, once per asset) then clamps to `BoundingBox(float3.Zero)`
  — a spamming mesh warns once and silently renders as a point-bounded object thereafter.

- **`Renderite.Shared.MeshUploadHint`** (struct, the `protected MeshUploadHint uploadHint` field) is a `Flag` bitmask of which vertex streams changed since last upload. `SetAll()` marks everything dirty (full re-upload, done every `PrepareMeshUpdate`); `ResetAll()` clears; indexer `hint[Flag.X]=bool`; `ResetUnusedChannels(meshx)` drops flags for channels the `MeshX` doesn't actually have. `Flag` members include `Dynamic`, `Readable`, plus per-stream `Positions/Normals/Tangents/Colors`, `BoneBindings`, and `UV0s..UV7s` via `GetUVFlag(int)` (`MeshUploadHint.GetUVFlag` switches 0→UV0s … 7→UV7s, throws out of range). For incremental updates, set only the changed stream flags instead of `SetAll`.

- **Texture path (`FrooxEngine.ProceduralTextureBase`).** Override `UpdateTextureData(Bitmap2D)` / `UpdateTextureDataAsync(Bitmap2D)`, implement abstract `ClearTextureData()`. Sealed `UpdateAssetDataAsync(Texture2D)` calls `PrepareBitmap()`, awaits your `UpdateTextureDataAsync(tex2D)`, then `PostprocessTexture()` (`ProceduralTextureBase.UpdateAssetDataAsync`). You write into the `protected Bitmap2D tex2D` (an `Elements.Assets.Bitmap2D`) sized/typed by `GenerateSize`/`GenerateFormat`/`GenerateMipmaps`; upload via `SetFromCurrentBitmap(TextureUploadHint, AssetIntegrated)` with its own `TextureUploadHint uploadHint` and Sync fields `FilterMode/WrapModeU/WrapModeV/AnisotropicLevel/MipmapBias`. `BakeTexture()` mirrors the mesh bake. `ProceduralTexture3DBase` is the 3D analog (`Texture3D`/`Bitmap3D`).

- **Threading rule (the footgun).** `UpdateMeshData(Async)` / `UpdateTextureData(Async)` run on the **asset/background thread, NOT the world update thread** (the engine awaits them with `ConfigureAwait(false)`). Inside generation you may only touch the supplied CPU object (`meshx` / `tex2D`) and plain data — do **not** read/write Slot/component/field state there. Read any needed Sync inputs before generation is scheduled (in the update thread), or marshal with `await default(ToWorld)` for the slot-side bits, then `await default(ToBackground)` for the heavy build (the bake methods demonstrate this).

#### MeshX construction API (`Elements.Assets.MeshX`)

`MeshX` is the mutable CPU mesh you build inside `UpdateMeshData`. Channels are off by default — enable them, then add geometry; positions are single-precision `float3` (consistent with Slot transforms being `float4x4`).

- **Channels/flags**: set `HasNormals/HasTangents/HasColors/HasBoneBindings` and `HasUV0s..HasUV3s` (bools), or `EnsureNormals/EnsureTangents/EnsureColors(bool, default)` to toggle-with-fill. UV dimensionality is per-channel: `SetUV_Dimension(uv, 2|3|4)` / `HasUV_2D/3D/4D`, `SetHasUV_3D/4D`. `meshx.Profile` is a `ColorProfile` (procedural meshes default `Linear`).
- **Capacity**: `EnsureFreeCapacity(n)`, `EnsureVertexCount(n)`, `IncreaseVertexCount/IncreaseTriangleCount/IncreasePointCount`, `VertexCapacity`. Pre-sizing avoids per-add reallocations.
- **Submeshes (required to hold triangles/points)**: `AddSubmesh<TriangleSubmesh>()` (or `AddSubmesh(SubmeshTopology)`), `InsertSubmesh`, `GetSubmesh(i)`, `SubmeshCount`. A `TriangleSubmesh` is the usual target: `sub.AddTriangle(v0,v1,v2)` / `AddTriangle(v0,v1,v2, reverse)` / `AddTriangle(Vertex,Vertex,Vertex)`, `AddTriangles(count, collection)`, `SetTriangle`, `GetTriangle`. There's also a `PointSubmesh` for point clouds.
- **Vertices**: `AddVertex()` / `AddVertex(ref float3 pos)` returns a **`Vertex` struct cursor** (holds meshx+index+version). Set via the cursor: `v.Position`, `v.Normal`, `v.Tangent`/`Tangent4`, `v.Color`, `v.UV0..UV3` (or `SetUV(channel, ref uv)`, `SetUV_3D/4D`), `v.BoneBinding`, `v.Flag`. `*Unsafe` variants return `ref` to the backing array (skip version checks — fastest, no bounds/version guard). Bulk: `AddVertices(count, VertexCollection)`.
- **Triangles at mesh level**: `AddTriangle(v0,v1,v2, submesh)` (int indices) / `AddTriangle(Vertex,Vertex,Vertex, submesh)`; the `Triangle` struct cursor exposes `Vertex0/1/2`, `Set(...)`, `ReverseWinding`, `SurfaceNormal`, `Area`, barycentric interpolation. Winding/normals helpers: `ReverseWinding()`, `FlipNormals()`, `MakeDualSided()`, `RecalculateNormals()` / `RecalculateNormalsMerged(cellSize)`, `RecalculateTangents(...)` / `RecalculateTangentsMikktspace(uvChannel)`.
- **Direct raw access (fast paths)**: `AccessRawPositions()/RawNormals/RawTangents/RawColors()` and `AccessRawUVs(i)` return `Span<T>` over the live arrays; `RawPositions/RawNormals/...` properties return the arrays; `GetPosition(i)`/`GetNormal(i)` return `ref`. Bulk-set with `SetVertexCount(n)` then fill spans, rather than per-vertex `AddVertex`.
- **Bones/skinning**: `AddBone(name)`, `BoneCount`, `ComputeBoneBindings(bones)`; per-vertex `BoneBinding` struct holds up to 4 (`boneIndex0..3`/`weight0..3`), `AddBone(idx,weight)`, `Sort()`, `Normalize()`, `Trim(threshold)`. `MeshX.SortTrimAndNormalizeBoneWeights(thresh, maxBoneCount)`, `NormalizeBoneWeights()`. Blendshapes: `AddBlendShape(name)`, `RecalculateBlendshapeNormals()`.
- **Bookkeeping**: `Clear()` (full), `ClearVerticesAndIndicies()`, `ClearSubmeshes()`; `Translate/Rotate/Scale`, `CalculateBoundingBox()`, `Append(...)/Copy(...)` with a `GeometryMask`. `CalculateBoundingBox()` is what feeds upload bounds unless you set `OverrideBoundingBox`.
- Canonical body shape: `meshx.Clear(); meshx.HasNormals = true; meshx.HasUV0s = true; var sub = meshx.AddSubmesh<TriangleSubmesh>(); var a = meshx.AddVertex(ref p0); a.Normal = n; a.UV0 = uv; … sub.AddTriangle(a.Index, b.Index, c.Index);` — then the engine uploads via `SetFromMeshX` automatically.
