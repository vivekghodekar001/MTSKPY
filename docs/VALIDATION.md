# Validation evidence — JSR reconstruction checkpoint 0.5

## Environment

Local SDK: .NET 10.0.403, installed under the workspace using Microsoft's dotnet-install script. Runtime reported by that SDK: .NET and Windows Desktop 10.0.14, Windows x64. No system PATH change is required.

## Automated checks

| Runner | Checks | Scope |
|---|---:|---|
| Compatibility | 17 | Existing login envelopes, form encoding, secret handling, session clearing, inspection/milestone validation |
| Transport | 7 | Fresh-request bounded GET retry; no login replay; HTTP cancellation; sanitized diagnostics/profile; 401 expiration |
| ViewModels | 20 | Shell behavior plus JSR conditional visibility, provider validation, photo commands, draft saving and validation-before-save |
| UiRegression | 18 | Existing draft/JPEG behavior, shell renders, source-derived JSR controls, selector bindings, native popup part, frozen thumbnails and network-path rejection |
| Jsr | 17 | Source option/condition parity, named photo roles, original JSON preservation, cancellation-safe persistence and malformed draft rejection |

These are executable test runners, not discovered `dotnet test` projects. All service responses are synthetic and injected. No production API requests or record submissions are used for these checks.

Software-rendered views are written to `work/qa-net10` in the enclosing workspace. They do not establish interactive desktop behavior, actual account permissions, Android visual parity, location suitability, or production backend compatibility.

## Build evidence

The nine-project Release solution builds with zero warnings/errors. All 79 executable checks passed after the final code changes. A self-contained win-x64 Release publish completed successfully and includes .NET / Windows Desktop runtime 10.0.14. Earlier previews remain available as baselines.

Six architectural checks passed: the four project-dependency boundaries and absence of business/transport work in the main and JSR windows' code-behind. The legacy inspection editor is explicitly excluded from any claim of complete MVVM migration.

## Known gaps

- No interactive desktop verification of preview 0.5 has been performed.
- The user reported that the phone is unavailable. New JSR runtime comparison against the Android screen is pending.
- The earlier user-confirmed sign-in success applies to preview 0.3, not independent testing of the migrated shell.
- Inspection and JSR transport remains unresolved. The historical token requirement was not established as an endpoint requirement.
- Original offline editor visual parity was rejected by the user; retaining it is preservation, not completion.
- Full MVVM extraction of that editor, persistence adapters, missing modules, hardware integration and authorized end-to-end verification remain required.
