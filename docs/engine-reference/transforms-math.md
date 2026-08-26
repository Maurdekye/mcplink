# Resonite Transforms & Math

> Resonite transforms and Elements.Core math reference (ILSpy and live-verified) — float4x4/floatQ single precision, coordinate system, TRS/decompose/inverse, the Slot space-transform pipeline, MathX quirks (clamped Lerp, nlerp, Repeat), and the slot transform-filter footguns (NaN to Zero, Inf to One, non-unit rotation to Identity).

## Math reference (Elements.Core + Slot transforms)

ILSpy-verified facts for reasoning about transforms/math. `float4x4`/`floatQ` are single precision (already noted in §2); details below.

**Coordinate system.** Left-handed, **Y-up, Forward=+Z**, Right=+X, meters (`float3.Forward=(0,0,1)`, `Up=(0,1,0)`, `Right=(1,0,0)`; `floatQ.LookRotation` builds side = `Cross(up, forward)`).

**`float4x4` layout & semantics.** Row-major, 16 readonly fields `mRC` (row R, col C); `this[r,c] = this[c + r*4]`; `ToString` prints rows. **Translation is the 4th column** `(m03,m13,m23)` = `DecomposedPosition`. Ctor `float4x4(row0..row3)` takes rows; `FromColumns(...)` takes columns.
- `operator*(float4x4, float3)` does a **perspective divide** (builds `float4(v,1)`, multiplies, returns `xyz/w`); `TransformPoint(p)` routes through it. To avoid the divide use `TransformPoint3x4` (upper 3×4, applies translation, no divide), `TransformVector` (3×3 linear part, ignores translation), or `TransformDirection` (normalizes then rescales). `operator*(float4x4, float4)` is the plain `M*v`.
- float↔double matrix/quaternion conversions are **implicit when widening** (`float4x4`→`double4x4`, `floatQ`→`doubleQ`), explicit when narrowing (`float4x4`→`float3x3`/`float2x2`). So a `float4x4` passes where `double4x4` is wanted with no cast.

**TRS composition (`float4x4.SetTransform`/`Transform`).** Builds **M = T · R · S** in one matrix: each rotated basis column is pre-multiplied by the per-axis scale `(x2,y2,z2)`, position drops into `m03/m13/m23`, bottom row `(0,0,0,1)`. Scale is object-space, applied first. This is exactly what `Slot.EnsureValidTRS` caches into `_cachedTRS`.
- `Slot.TRS` setter does the inverse: `value.Decompose(out pos,out rot,out scale)` then assigns **Scale, then Position, then Rotation** (that order). `HasIdentityTransform` = pos==Zero && rot==Identity && scale==One.

**Decompose loses reflections (mirror/negative scale).** `float4x4.Decompose` extracts `scale` = **always-positive** column magnitudes (`sqrt(sum of squares per column)`), then divides them out to recover rotation. A negative-determinant (mirrored) basis decomposes to all-positive scale + a wrong rotation — the reflection is silently dropped. `DecomposedScale/Rotation/Position` use the same logic.

**Inverses fail silently to Zero, not Identity/throw.**
- `float4x4.Inverse` returns `float4x4.Zero` (all zeros) when `det==0`. So inverting a degenerate (e.g. zero-scale) transform yields a zero matrix.
- `AffineInverseFast`/`SetAffineInverseFast` use **full 3×3 cofactors** (correctly inverts non-uniform scale + shear, not just rotation+uniform-scale) and set translation to `-(inv3×3 · t)`, forcing bottom row `(0,0,0,1)` — valid for affine only (no perspective). Bails to `default`/`Zero` when `Determinant3x3==0`. `Slot.GlobalToLocal` is this inverse of `LocalToGlobal`.

**Slot space-transform pipeline (single precision throughout).**
- `Slot.GetLocalToSpaceMatrix(space) = space.GlobalToLocal.MultiplyAffineFast(LocalToGlobal)`; "space" is just another slot you compose against the inverse of. Global TRS is built recursively up the chain: `Parent.LocalToGlobal.MultiplyAffineFast(TRS)` (`EnsureValidLocal2Global`); `MultiplyAffineFast` ignores the bottom row.
- **Point vs vector vs direction are distinct ops — picking wrong silently mishandles translation/scale.** `LocalPointToParent`→`TRS.TransformPoint3x4` (with translation); `LocalVectorToParent`→`TRS.TransformVector` (no translation); `Parent*ToLocal` use `TRS.AffineInverseFast.*`; Direction variants are rotation-only.
- `Slot.GlobalRigidTransform` = `RigidTransform(GlobalPosition, GlobalRotation)` — **drops scale** (RigidTransform is position+rotation only).
- **Why global scale is approximate** (the §1 caveat, concretely): `LocalScaleToGlobal/GlobalScaleToLocal(float3)` are a componentwise multiply/divide by `LocalToGlobal.DecomposedScale` (the positive column magnitudes) — they ignore rotation between a non-uniformly-scaled parent and child, and ignore shear; exact only when intermediate scales are uniform/axis-aligned. The scalar `(float)` overloads are worse: they collapse the result via `MathX.AvgComponent` (mean of the 3 axes).

**Rotations (`floatQ`, four `float` fields x,y,z,w).**
- `Euler`/`AxisAngle`/`DeltaRotation` and the `EulerAngles` property are in **DEGREES**; the `*Rad`/`EulerAnglesRad` twins are radians. Properties: `Identity`, `Magnitude`, `Normalized`/`FastNormalized`, `Conjugated`, `Inverted`, `IsIdentity`, `IsValid`, `IsNaN`, `IsInfinity`. `Slot.*RotationToGlobal` use a cached `LocalToGlobalQuaternion` + `floatQ.InvertedMultiply`.

**`MathX` quirks (footguns).**
- **`Lerp` is CLAMPED to [0,1]** (returns `a` if `t<=0`, `b` if `t>=1`) across all overloads — use `LerpUnclamped` to extrapolate. Also `InverseLerp`, `Remap(v,inMin,inMax,outMin,outMax)`, `Remap11_01` ([-1,1]→[0,1]).
- Quaternion `Lerp(floatQ,floatQ,t)` is a clamped **nlerp** (componentwise lerp then `FastNormalized`), **not** slerp — `Slerp`/`SlerpUnclamped` are separate. (`floatQ.Slerp` takes `a` by ref, `b` by value.)
- `SmoothLerp(current, target, ref intermediate, delta)` is a **caller-state double-lerp**, NOT a spring and NOT framerate-corrected: `delta*=2; delta=Clamp01(delta); intermediate=Lerp(intermediate,target,delta); return Lerp(current,intermediate,delta)`. Caller must persist `intermediate` between frames.
- **`Repeat` int/float semantics differ.** Float/double/decimal `Repeat(v,length) = v - Floor(v/length)*length` → `[0,length)`, handles negatives. Integer `Repeat(int val, int max)` does `max++` first → second arg is an **inclusive max**, range `[0,max]`; `uint`/`ulong` special-case `max==MaxValue`→`val` and `max==0`→`0`.
- `FilterInvalid` (vectors/matrices/quaternions/colors, optional fallback) and `floatQ.Filtered` scrub NaN/Infinity to identity-or-fallback — the idiomatic degenerate-value cleanup, alongside per-type `IsNaN`/`IsInfinity`/`IsValid`.

**Constants & tolerances.** `MathX` named constants are **`public static` FIELDS, not C# `const`** (reflection sees fields): `PI`, `TAU`, `HALF_PI`, `QUARTER_PI`, inverses, `SQRT2`, `E`, `PHI`, `Deg2Rad`, `Rad2Deg`, plus `FLOAT_EPSILON`/`DOUBLE_EPSILON` and looser `APPROXIMATELY_FLOAT_EPSILON`/`APPROXIMATELY_DOUBLE_EPSILON`.
- `MathX.Approximately(a,b,eps=1e-6f)` is a hybrid absolute+relative test: `Abs(a-b) < Max(1e-6*Max(Abs(a),Abs(b)), eps)` (relative factor 1e-6 for float, 1e-12 for double). Quaternion `Approximately` uses `Dot(a,b) >= 1-eps`.

**Misc.** `Optional<T>` is **not** in Elements.Core (it lives in FrooxEngine/ProtoFlux); Elements.Core's nullable/result helpers are `CoderNullable`, `NullableFieldProxy`, `Result`. `floatQ`/`colorX`/vectors expose an exhaustive read-only **swizzle** property surface (`xyz`, `wzyx`, `rgb`, `bgra`, …) where an underscore lane means **0-fill** (e.g. `_x`, `x_z`, `___w`).

## Slot transform-write filters (live-verified — important footgun)

`Slot` runs every transform write through `PositionFilter`/`ScaleFilter`/`RotationFilter` (`FrooxEngine.Slot`). Confirmed by live ResoniteLink tests on Build 2026.6.19 (writing `1e40` overflows single-precision `float` to `+Infinity`, tripping the `IsInfinity` path):
- **Position** — NaN/Infinity → the *entire* `float3` becomes `float3.Zero` (not just the bad lane). Live: `(1e40, 5, 7)` → `(0,0,0)`.
- **Scale** — NaN/Infinity → `float3.One`. Live: `(1e40, 2, 3)` → `(1,1,1)`.
- **Rotation (the footgun)** — `RotationFilter` is `value.IsValid ? value.FastNormalized : floatQ.Identity`, and **`floatQ.IsValid` is `0.9 < SqrMagnitude < 1.1`** (`Elements.Core.floatQ.IsValid`, decompiled). So a rotation write whose quaternion is **not already near-unit is silently reset to `floatQ.Identity` — it is NOT normalized for you**. `FastNormalized` only cleans up drift within ±2.1e-8 of unit on an already-near-unit value. Live: `(1,1,1,1)` (SqrMag 4) → Identity; `(3,0,0,4)` (SqrMag 25) → Identity; `(0,0.7071,0,0.7071)` (SqrMag 1) → unchanged. **Always feed slot rotations an already-normalized quaternion** (e.g. from `floatQ.Euler`/`AxisAngle`/`LookRotation`, which return unit quaternions). This corrects the looser "always normalized on write" phrasing.
- The global setters differ: `GlobalRotation` setter applies only if `value.IsValid` (else no-op — keeps the old rotation rather than snapping to Identity); `GlobalScale` setter only if `!IsNaN && !IsInfinity`.

*Source-grounded via ILSpy + live ResoniteLink verification against Resonite Build 2026.6.x. Constants are build-pinned.*
