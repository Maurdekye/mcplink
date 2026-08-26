# Resonite Hard Limits & Constants

> Resonite hard limits and engine constants reference (ILSpy-verified, Build 2026.6.x) — session/user caps, undo, time/physics timestep caps, collider/locomotion clamps, texture/audio/OSC/cloud-variable limits.

## Hard limits & engine constants (ILSpy-verified)

Values that silently clamp/cap or gate behavior. All single-precision `float` unless noted — very large/small inputs saturate rather than error.

**Session & identity.**
- A world is capped at **255 concurrent users** by the `RefID` bit layout: a single `ulong` split into 8 low user bits (`USER_MASK 255`, `MAX_USERS 255`) + 56 position bits. `LOCAL_ID = 255` is reserved to mark a local (non-synced) id — `IsLocalID = (id & 0xFF) == 255`. `RefID.ToString()` = `"ID"` + hex; `TryParse` requires the `"ID"` prefix and length ≥ 3. (`Elements.Core.RefID`)

**Undo (`FrooxEngine.Undo.UndoManager`).**
- Default **50 steps** (`MaxUndoSteps`, set in `OnAwake`). History is **not** an in-memory stack — each user gets a child slot `"User: <UserID>"` under the (protected, non-persistent) UndoManager slot, each action its own slot/component ordered by `OrderOffset`; `TrimExcessNumber` destroys oldest once count > max. Redo = "unperformed" actions in the same list; performing a new action trims all reversed ones first.

**Time & stepping.**
- `World.Time.Delta = min(RawDelta, MAX_TIMESTEP=0.1f)` — a hitch never reports >100 ms of delta. `SmoothDelta` is clamped to ±10%/frame. `WorldTime` accumulates as a **`double`** (`WorldTimeFloat` is the float cast); authority time only snaps in if it differs by `> MAX_WORLDTIME_LAG=0.5s` and ≥ `ADJUST_COOLDOWN=5s` since the last snap; negative measured deltas reuse the prior frame. (`TimeController.Update`)
- Physics steps independently: `delta = min(World.Time.Delta, MAX_DELTA=0.05f)`, interpolated toward target at `DELTA_INTERPOLATION_SPEED=0.025`, clamped to ±`MAX_DELTA_DELTA=0.005`/step. (`PhysicsSimulation`)

**Colliders & physics (`FrooxEngine.Collider`, `PhysicsSimulation`).**
- Size clamped to **[1e-6, 1e6]** (`MIN_SIZE`/`MAX_SIZE`); `ProcessColliderSize` does `size *= Slot.GlobalScale` then `FilterInvalid`→`Abs`→`Clamp`, so dims are always positive and scaled by global scale. Offset clamped to ±1e6 (`ProcessColliderOffset`); position clamped to **±1e8** (`MAX_POSITION`, `ClampPosition`); mass clamped to [1e-6, 1e6] (`ComputeActualMass`). `DEFAULT_SPECULATIVE_MARGIN=0.1`. `PhysicsSimulation` mirrors these + `MAX_BOUNDS_SIZE=1e10`.
- `Slot._isUniformScale = Abs(sx-sy) < 0.01 && Abs(sx-sz) < 0.01` (absolute 0.01 tolerance). Non-uniform slots escalate child cache-invalidation flags to 62 (force re-eval of local2global incl. scale) for all descendants; uniform ones propagate only the passed flags. (`Slot.SlotTransformChanged` / `InvalidateLocal2Global`)

**Locomotion (`FrooxEngine.CharacterController`, `UserPoseController`).**
- `CharacterController.MAX_SLOPE = 3.138451 rad` (~179.8°) caps `MaximumSupport/TractionSlope` before cosine. `OnAwake` defaults: `Margin 0.05`, `StepUpHeight 0.5`, `StepUpCheckDistance/EdgeDetectionDepth 0.25`, `Gravity down*9.81`, `Speed 4`, `SlidingSpeed 3`, `AirSpeed 1`, `TractionForce 1000`, `SlidingForce 50`, `AirForce 250`, `MaximumGlueForce 5000`, `MaxTractionSlope 45°`, `MaxSupportSlope 75°`, `JumpSpeed 6`, `SlidingJumpSpeed 3`; scaling Mass/Force=Cubic, Speed/Jump/Gravity=Linear; default capsule mass 10; legacy width upgrade 0.2→0.275.
- `UserPoseController` locomotion sim is sub-stepped: `MAX_SIMULATION_TIMESTEP=0.01` (10 ms), `MAX_SIMULATION_STEPS=10`. `MAX_HEAD_ANGLE=80`, `MAX_MOVEMENT_DEVIATION_ANGLE=45`; in-air detect `IN_AIR_DELAY_TIME=0.2s` / `IN_AIR_DELAY_MAX_VELOCITY=4`; default `BodyHorizontalAngle=30`.

**Assets & audio.**
- Texture POT handling: `Elements.Assets.TextureSize {NPOT, LowestPOT, NearestPOT, HighestPOT}`; `StaticTexture2D.PowerOfTwoAlignThreshold=0.05` decides near-POT snapping. Max pixel count is **dynamic** (engine `TextureQualitySettings` via `AssetManager.GetMaxTextureSize`→`TextureSettings.GetMaxSize`), *not* a constant; over-cap throws `TextureSizeException(int2 size, int maxPixels)`.
- Audio playback rate capped at **32x** (`AudioClipPlayerBase.MAX_PLAYBACK_SPEED=32`; `ERROR_FADE_SAMPLES=256`).

**Generic value types.**
- `Elements.Core.ReflectionExtensions.MAX_VALUE_SIZE=4096`: `IsValidGenericType` rejects a spherical-harmonics type whose `SphericalHarmonicSize()` (coeff count × element unmanaged size) exceeds 4096 bytes (guard against absurd generic value-type instances). SH order helpers `SphericalHarmonicsL1..L4` via `SphericalHarmonicsHelper.CoefficientCount`.

**OSC (`FrooxEngine.OSC_Sender`).**
- `MAX_ARG_COUNT=256` (indices outside [0,256) silently dropped). `IsConfigurationValid` requires URL scheme `udp`/`osc`, IPv4 host, remote port (0,65535], `LocalPort` [0,65535]. `AutoResendInterval` defaults `+Inf` (no resend); default `SendMode = SendIndividually`; sending needs a granted `HostAccessPermission` (`HostAccessScope.OSC_Sender`).

**Cloud variables (`SkyFrost.Base.CloudVariableHelper`).**
- `MAX_SUBPATH_LENGTH 256`, `MAX_STRING_LENGTH 8192`, `DEFAULT_MAX_STRING_LENGTH 256`, `MAX_URI_LENGTH 512`, `MAX_VARIABLES_PER_USER 256`, `MAX_VARIABLES_PER_GROUP 8192`, `DELIMITER ';'` (permission lists). A plain `string` value caps at 256 chars; `string:N` allows up to N (N ∈ 1..8192); `uri` max 512.
