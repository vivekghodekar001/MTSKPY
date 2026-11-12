# One live submit - MT4420200404278

**Consumer: MT4420200404278** (SANTOSH GANGARAM PABALE, cpf 02284251,
vendor 100036833 ULTECH ENERGIES, inst date 23-Dec-2025).

Submitted from the published build on 2026-11-12 at 09:36:53.

## Why this one

It is a **returned** record: the office sent it back with

> Upload actual site clear photos with Lineman in uniform, Complete system,
> Water discharge, Water source

so the revert field is exercised, which the plain pending records do not
do. It was checked against the service before the submit:

* it was in the server's pending list for cpf 02284251 (41 rows) - not a
  stale local row;
* it was **not** MT4420200801626, the consumer the portal already shows as
  PUMP INSTALLTION INSPECTION DONE BY LINEMAN.

Six other returned records were pending and could have been used instead
(MT4420200409971, MT4420200467931, MT4420200969753, MT4420200992693,
MT4420201046315, MT4420201055563).

## What was sent

* the installer, taken from the record itself: **Vishal thombare** /
  **7397926761** (the record's own `vendor_person_name` and
  `vendor_person_mobile_no`; the form opens with those fields empty on
  purpose);
* three DIFFERENT site photos - the duplicate gate compares the source
  bytes, and the three assets in the workspace turned out to be the same
  file, so three distinct JPEGs were made for this submission
  (34,145 / 35,896 / 32,542 bytes);
* both signatures, drawn separately (1,149 B and 1,222 B - different
  bytes, so the signature gate did not trip either);
* the location the app resolved:
  `Source: InstallationCoords · Lat=19.336027 · Long=74.095619 ·
  Accuracy=0 · Mocked=True · Distance from installation=0 m · OK` - the
  Windows geolocator is not available on this machine, so the app falls
  back to the record's installation coordinates with **accuracy 0**.

## The manifest (pre-send)

```
data  (text/plain, size set at submit)
Insp-MT4420200404278-setupPhotoWithBen.jpg  (image/jpeg, 34145 B)
Insp-MT4420200404278-manualHandOverPhoto.jpg  (image/jpeg, 35896 B)
Insp-MT4420200404278-waterFlowPhoto.jpg  (image/jpeg, 32542 B)
Insp-MT4420200404278-benSign.png  (image/png, 1149 B)
Insp-MT4420200404278-vendorRepSign.png  (image/png, 1222 B)
```

After the submit the data part carries its real size (2258 B) and the
location line appears under the parts.

## The server's answer

```
Submit complete (HTTP 2xx). Body shown above.
data.json written: ...\inspection-data\data-MT4420200404278.json

HTTP 200 OK

{"message":"Survey uploaded successfully."}
```

## The verify path

```
Verify complete.
HTTP 200 OK

{"message":"Survey uploaded successfully."}

--- VERIFY ---
Verify: count=0
```

count=0 is the accepted state: the record has left the officer's pending
list, so a re-read does not find it there any more.

## Accepted, and gone from the queue

The live store (consistent sqlite `backup()` snapshot):

```
record                        MT4420200404278
  status                      3   (Accepted)
  cpf_no                      02284251
  seen_cpfs                   02284251
  last_server_message         {"message":"Survey uploaded successfully."}
  last_server_message_at      2026-11-12T04:06:56Z

My pending for 02284251       40   (was 41 before the submit)
the record is in the queue    no
```

And the service agrees - the server's pending list for cpf 02284251 is now
**40 rows** and MT4420200404278 is no longer in it.

## The blob that went up

46 keys, exactly as the contract requires:

```
username             02284251          (the session loginId)
cpf                  02284251          (the record's cpf_no)
vendorCode           100036833         (the record's own vendor_code)
consumerNumber       MT4420200404278
lat / long           19.336027 / 74.095619
locationAccuracy     0
locationMocked       True
distanceFromInst     0
vendorPersonName     Vishal thombare
vendorPersonMobile   7397926761
commDate             {value: 23-Dec-2025, isOK: Y, remark: }
pumpLoad             {value: 5 HP, isOK: Y, remark: }
```

`reason_to_revert` is **not** in the blob - it is never uploaded.

## Files

`j8-pending-queue.png` (the officer's queue with 41 pending),
`j8-manifest.png` (the form with the photos, the signatures, the installer
and the manifest), `j8-accepted.png` (after the submit and the verify),
`j8-manifest.txt`, `j8-verify.txt`, `j8-submit-response.txt`.
