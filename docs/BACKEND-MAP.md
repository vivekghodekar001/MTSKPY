# Backend inventory and evidence

Evidence for the following routes is the supplied APK's recovered bundle and the existing version 0.3 API adapter. No new backend or endpoint was created. Production behavior has not been retested during this migration.

| Operation | Method / existing route | Request/response evidence | Current verification |
|---|---|---|---|
| Employee sign-in | POST `mobileapp.mahadiscom.in/mskpy/SignIn/Employee` | Form fields; success/user envelope in existing adapter and APK | User reported successful sign-in in preview 0.3; injected-handler regression tests pass |
| Agency sign-in | POST `mobileapp.mahadiscom.in/mskpy/SignIn/Agency` | Form fields plus scheme; same envelope | Request formatting tested offline; live role compatibility not established |
| TOD vendor sign-in | POST `nvp.mahadiscom.in/vendor-meter-api-1.0-SNAPSHOT/api/v1/login` | JSON request; SUCCESS response and issued token | Offline request/response contract tests only |
| Milestone applications | GET `mobileapp.mahadiscom.in/mskpy/Mskvy2Application/Bulk` | Existing first-page query and applications envelope | Adapter preserved; no current live result verified |
| JSR applications | Existing JSR applications route in prior adapter | APK/client evidence; disputed authentication assumptions recorded in handoff | Unresolved; do not infer a token requirement from the current client's guard |
| Inspection applications | Existing inspection applications route in prior adapter | APK/client evidence; synthetic local transport capture exists | Synthetic mock only; no new production identity-based retrieval performed |
| Surveys, installation, groundwater, vendor survey, TOD uploads | Routes/operations found in recovered bundle | Reference code requires additional validation against original UI and legitimate integration | No production submission adapter added in this checkpoint |

Authentication errors, TLS failures, session expiration and service schema errors must remain distinguishable. Do not silently remove authorization/integrity checks, impersonate a different device, log credentials, invent a missing backend operation, or treat a mocked record as real inspection evidence.

The local integration harness under `work/mock-e2e` is separate and restricted to synthetic identities and a fixed loopback destination. Its successful POST is not production compatibility evidence and is not integrated into this desktop shell.
