# Changes for RageCoop maintainers

## 🇬🇧 English · [🇷🇺 Читать по-русски →](ENGINE_CHANGES.ru.md)

These changes target the **tested 1.5.12.15 binary set**, identified in the release manifest. They are applied through guarded Harmony patches, not claimed as fixes already merged upstream. Unknown method shapes are rejected. Current nightly binaries differ; applicability to them is not established.

## Confirmed issues in the tested engine

| Area | Observed issue | Addon change |
| --- | --- | --- |
| Vehicle creation | Unbounded sleep loop in a game callback | Bounded frame-based requests and session cancellation |
| Projectile cleanup | Wrong new-projectile candidate; missing shooter interrupted cleanup | Candidate identity checks and cleanup that tolerates a missing shooter |
| Player collections | Readers raced with roster/index mutation | Short coordinated operations and snapshots |
| Resource transfer | Concurrent transfers shared a stream cursor; same-sized downloads could be mistaken for current content | Coordinated stream access and content-derived names for this resource |
| Server requests | Response-table races and timeout/sender mismatches | Bounded waits, sender/type/state validation and cleanup |
| Entity lifecycle | Deferred removal could act on a replaced connection or owner | Recheck active session and ownership before deletion |
| Packet parsing | Lengths could cause allocation before checking remaining data; asymmetric codecs | Bounds before allocation and symmetric round trips for affected values |
| Fragment retention | Fragment groups and large cached buffers lacked sufficient bounds | Size/count limits, expiry and disconnect/shutdown cleanup |
| Transport | Unreliable state shared an ordered delivery channel with unrelated state | Separate delivery rules for the supported transport paths |
| Settings and logs | Invalid settings could be overwritten; logger locked a replaceable string | Read-only validation/atomic initialisation and a stable logger monitor |
| Client loading | Resource/native loading and global certificate bypass needed stricter boundaries | Early loading/path guards and removal of the certificate-accept-all assignment |

## Known issues

- A fully repaired vehicle may retain an older damage state for another player.
- Some keyboard layouts may have input issues.
