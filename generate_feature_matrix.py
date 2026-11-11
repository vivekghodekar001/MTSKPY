import json
import pathlib

project = pathlib.Path(__file__).resolve().parent
workspace = project.parents[1]
inventory = json.loads((workspace / 'outputs/mskpy-reconstruction/inventory.json').read_text(encoding='utf-8'))
rows = [
    ('Employee / agency login', 'APK Login and processLoginMobileApp; 0.3 login success user-reported', 'MVVM UI and existing request formats migrated', 'Offline contract tests; 0.4 live sign-in not retested'),
    ('TOD vendor login', 'Separate processLoginTODMeter route recovered', 'Existing adapter retained; scheme selector bound', 'Offline only'),
    ('Session lifecycle', 'Secure-store/relogin code recovered', 'Memory-only session; cancellation, sign-out and 401 expiration tested', 'Persistence parity incomplete; no saved passwords'),
    ('Home navigation', 'Three cards observed on the reference account', 'Bound card inventory; keyboard-friendly sidebar', 'Synthetic renders; agency role/module parity unknown'),
    ('Application search / details', 'List/search/detail flows recovered', 'Bound list, local filtering and details; 100 displayed matches', 'View-model tests; backend compatibility remains partial'),
    ('Milestone application list', 'MSKVY2Home and existing GET format', 'Existing first-page adapter retained', 'No current live list result verified'),
    ('Milestone survey / photos', 'milestone-survey and viewphoto routes; remark/photo rules recovered', 'Validation rules retained', 'Editor and submission incomplete'),
    ('JSR application list', 'JSRHome route and client code', 'Existing adapter preserved', 'Unresolved live transport; do not assert missing-token requirement'),
    ('JSR survey', 'Exact 19-field schema, source option values, workflow-122 condition and conditional validation recovered', 'MVVM form, three named JPEG roles, source-preserving local drafts and conditional validation implemented', 'Source-derived and offline tests; Android runtime comparison and authorized live submission pending'),
    ('Line-staff inspection', 'inspection/sign routes; copied 23-check schema', 'Legacy offline editor retained as a transitional view', 'Offline regression tests; user rejected original visual fidelity'),
    ('Photo gallery selection', 'Patched APK includes gallery-related behavior', 'Three local JPEG slots with decoding checks', 'Does not establish camera/provenance/metadata parity'),
    ('Signatures', 'Signature route and ink workflows identified', 'Legacy beneficiary/vendor pads and local draft round-trip retained', 'Synthetic in-process checks; no live evidence submitted'),
    ('Location / distance checks', 'Location state and distance-policy fields found', 'Not implemented for live workflows', 'Unknown hardware suitability; no synthetic location presented as real'),
    ('Pump installation', 'installation, barcode and sign routes', 'Not implemented', 'Needs screen/field/authorized-contract verification'),
    ('Groundwater data', 'GWR list/upload routes', 'Not implemented', 'Static route evidence only'),
    ('Vendor survey', 'vendor-sc route and constants usage', 'Not implemented', 'Static route evidence only'),
    ('TOD reading / photo viewing', 'TOD list, upload and photo routes', 'Not implemented beyond login adapter', 'Static route evidence only'),
    ('Profile', 'Profile route recovered', 'Bound sanitized profile fields', 'Synthetic display checks; exact layout parity pending'),
    ('Settings / language', 'Settings and translation code recovered', 'Read-only status display; reference dark palette', 'Theme/language interaction and persistence incomplete'),
    ('Diagnostics', 'dignostics route recovered', 'Response-structure diagnostics and command-based copy', 'Secret omission tested; original diagnostic parity incomplete'),
    ('Remote constants / configuration', 'Constants/config usage recovered', 'No new production integration added', 'Requires authoritative behavior validation'),
    ('Timeouts / retries / API errors', 'Existing adapter inspected; user engineering requirements', '35-second attempt timeout, cancellation, one bounded GET retry, 401 handling', 'Injected HTTP-handler integration tests'),
    ('Offline persistence / queue', 'Original complete sync semantics not established', 'Explicit legacy JSON draft save/reopen retained', 'SQLite deferred until a verified requirement exists'),
]
lines = ['# Feature-parity matrix', '', 'Checkpoint: .NET 10 source-derived JSR reconstruction preview 0.5. No row implies complete application parity. Confirmed APK code, user reports, synthetic tests and unverified behavior are intentionally distinguished.', '', '| Feature | Android/reference evidence | Windows implementation | Verification / remaining gap |', '|---|---|---|---|']
lines += ['| ' + ' | '.join(row) + ' |' for row in rows]
lines += ['', '## Exact recovered route inventory', '', 'The following 36 entries include layout files; they are not 36 independently verified end-user features. Source: the exact supplied bundle disassembly, recorded in the existing inventory.', '']
lines += ['- `' + path + '`' for path in inventory['routes_from_exact_apk_disassembly']]
lines += ['', '## Acceptance required before feature-complete release', '', '- Compare each screen and conditional flow against the Android reference using appropriate account roles.', '- Validate legitimate backend request/response behavior and permission boundaries without bypasses or invented endpoints.', '- Exercise camera/gallery, signature, barcode and real Windows location behavior where required; record hardware limitations.', '- Test offline/online transitions and recovery using verified original semantics.', '- Confirm no regressions in the existing working account flow.', '- Complete the legacy editor migration and full accessibility/keyboard/window-size testing.', '- Verify the packaged application on its intended Windows environment; a release configuration is not proof of feature completeness.']
(project / 'docs/FEATURE-PARITY.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
print(f'Documented {len(rows)} feature groups and 36 recovered route/layout entries.')
