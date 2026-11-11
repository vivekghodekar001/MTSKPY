# Active reconstruction record

## Deliverable constraint

A standalone native Windows x64 application using .NET 10/WPF/MVVM must reproduce verified Android screens and workflows through legitimate backend integration, without requiring a connected phone or emulator. Full parity must be demonstrated before completion is claimed.

## Operating rules

- Preserve the original APK, prior previews and source evidence.
- Use the exact recovered Hermes bundle as the reference; label source-derived behavior separately from runtime verification.
- Preserve backend authorization/integrity requirements. Do not invent endpoints, copy phone tokens, or construct a production submission from synthetic evidence.
- Build and test each significant workflow increment. Use synthetic records for automated tests and render QA.
- Record unresolved backend behavior rather than presenting a mock result as live compatibility.

## Confirmed facts

- Observed: SDK 10.0.403 is installed under `work/tools/dotnet10`; preview 0.4 has four projects and a bound MVVM shell.
- Observed: 45 offline checks and five architectural checks passed for that checkpoint.
- Observed: original Hermes bundle and decompiled output remain in `work/exact-app`. The bundle matches the supplied APK and the previously connected phone's bundle.
- Observed: the JSR form's owning function and validation were located in the recovered JavaScript. That is code evidence, not runtime parity evidence.
- Observed: `adb devices -l` returned no connected devices at the start of the current increment.
- User-reported: the phone is not available now. Android runtime screen comparison remains blocked on device availability.
- Observed: the new JSR form has 19 source-derived values, exact dropdown choices, workflow-action-dependent vendor field, three named photo roles, and MVVM-local-draft behavior. The solution builds without warnings/errors.

## Refuted conclusions

- Absence of a token in the login response does not establish that inspection/JSR endpoints require one. The earlier token diagnosis was unsupported.
- A successful local multipart POST does not establish a complete production payload or Android feature parity.
- The existing generic offline inspection editor was rejected as visually unlike the reference; it is not the finished reconstruction.
- The earlier unconditional network-provider check was wrong. Recovered JSR source requires a selected provider only when mobileNetwork is Yes; domain and view-model regression tests now cover both branches.

## Dead routes / boundaries

- A phone-connected companion is outside the user's requested deliverable.
- Identity-only production record retrieval, bypassing authorization, and real-record/signature submission-payload reproduction are not part of the implemented integration.

## Current increment

The source-confirmed JSR form is implemented as an MVVM view, preserving source options and conditional validation, with application/infrastructure interfaces for local document interaction. Existing live adapter behavior is preserved. No new production submission was introduced. Source-derived form behavior and software rendering are being verified; Android comparison and live upload remain unverified.

## Open questions

- Runtime visual/layout parity cannot currently be compared on the disconnected phone.
- Legitimate live inspection/JSR compatibility remains unresolved.
- Camera, GPS, barcode, remote constants and full offline/synchronization behavior require further verification.
- Complete migration of the legacy inspection editor remains pending.

## Environment

Workspace root: `C:\Users\vivek\Documents\Codex\2026-11-07\https-github-com-fishforks-apk-reverse2`.
Build solution: `work/windows-next/Mskpy.sln`.
Latest released baseline before this increment: `outputs/MSKPY-Windows-net10-preview-04`.
