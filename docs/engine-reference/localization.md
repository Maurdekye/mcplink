# Resonite Localization

> Resonite localization reference (ILSpy-verified) — LocaleString is a struct (a bare string is not a key and shows verbatim), silent in-band key-resolution failure, and the additive locale-file fallback chain.

### Localization (LocaleString / LocaleResource)

- **`LocaleString` is a struct, not a string, and a bare string is NOT a key.** Fields: `content`,
  `format`, `isLocaleKey`, `isContinuous`, `arguments` (`Dictionary`). The implicit `string`→`LocaleString`
  conversion sets `isLocaleKey:false` (`LocaleString.op_Implicit`), so assigning a raw string to a
  `LocaleString` field displays it **verbatim, never looked up**. To actually localize, build the key with
  `str.AsLocaleKey(...)` (sets `isLocaleKey:true`, `LocaleHelper.AsLocaleKey`) or a
  `LocaleHelper.SetLocalized`/`DriveLocalized` helper.
- **Key resolution failure is silent and in-band** (`LocaleResource.Format`): null/whitespace key or null
  `Data` → `null`; an **unknown key returns the raw key string** (unless `returnNullIfNotFound:true` →
  `null`); a thrown formatting exception is caught and returns the literal `"ERROR!!!"`. So a key string or
  `ERROR!!!` showing in-world is a missing/bad-format key, not a crash. Global locale args are merged into
  every format call (`MergeGlobalArguments`).
- **Locale files load additively along a fixed fallback chain** (`LocaleResource.LoadTargetVariant`):
  ordered, de-duplicated list = `{mainLang}-x-{PrimaryGroupId}` (only `if Engine.InUniverse`) → exact
  `LocaleCode` → main language (e.g. `en` from `en-US`) → `"en"` (always last). Each is loaded from
  `{Engine.LocalePath}/{locale}.json` via `LoadAdditively`; **missing files are skipped silently**, more
  specific variants take precedence, and `en` is the universal fallback.
