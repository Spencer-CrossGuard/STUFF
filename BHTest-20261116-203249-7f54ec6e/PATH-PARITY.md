Optional read-only LDAP enrichment can supply missing imported group classification with retained provenance; controlled fixture replies reproduce all 3 stored ESC1 endpoint pairs, while offline import remains at 1 of 3. Post-export classifications make the affected dataset ESC1 projections conditional. Recorded prerequisite gaps explain known CA/template/group stops, not all possible causes of an arbitrary absent path. See README.md and ADCS-SCOPE.md.

Path rules 6 add ESC1 candidates for complete imported direct-root CA evidence, retaining separate template/CA-service enrollment, trust and host witnesses. LDAP-only inventory and unsupported cases remain excluded. See [ADCS-SCOPE.md](ADCS-SCOPE.md) for required evidence, tests and limits.

# BloodHound pathing comparison — November 16, 2026

**Full one-for-one BloodHound compatibility is not yet established.** The local BloodHound source defines 64 AD relationship kinds in its frontend pathfinding set. BHTest recognizes 29 of those for traversal. Recognition is not a claim that every kind is collected by ordinary LDAP, derived with all BloodHound prerequisites, or available in this lab. Azure and OpenGraph are outside that count.

Path rules 4 additionally model the backend-declared `GPOAppliesTo` kind under conservative computer-scope prerequisites. It is not in the 64-kind frontend set used for the comparison count. This does not enable raw Contains/GPLink traversal or claim full GPO parity; see GPO-SCOPE.md.

The reference is `C:\Users\Administrator\Desktop\Sharp\Bloodhound Real\BloodHound-main`. The comparison reads its actual source, not assumptions based on screenshots. `Compare-BloodHoundPaths.ps1` in the working source reproduces a CSV/JSON edge matrix and records reference file hashes. It does not run BloodHound's server or claim a live differential test.

## Implemented in this update

| Behavior | BloodHound reference | BHTest result |
|---|---|---|
| Two-ended search | `cmd/api/src/api/v2/pathfinding.go`: GetShortestPath returns all shortest paths and unique source/kind/target edges | Native viewer, collector Path Explorer, and CLI PathQuery return the union of every equally short directed route, including parallel permissions |
| Relationship selection | Inclusive/exclusive kind filters; UI passes selected kinds | Native viewer now offers a checked relationship filter; pair and target searches use it; clear-all remains empty. Collector Path Explorer keeps its existing selection UI |
| Filtering before limits | Query filters constrain the requested traversal | Target-only shortest mode excludes cyclic and longer detours before counting displayed edges |
| Limits | BloodHound reports query failures/timeouts instead of silently claiming a complete graph | Search visits, one-million search edges, and returned graph edges have separate budgets. Partial results are marked incomplete; cancellation/timeouts remain explicit |
| Edge identity | BloodHound deduplicates source/kind/target | Pair and target graph results deduplicate repeated traversal records from different supported buckets |
| Cycles and graph size | Shortest paths omit longer cyclic walks | The new engine keeps a shortest-route DAG; it does not enumerate exponentially many route permutations |
| Ordered waypoints | The reference frontend combines shortest-path queries between up to four ordered objects | The desktop viewer supports Source → Via 1 → Via 2 → Target; CLI PathQuery accepts up to two ViaIds. Each leg keeps all shortest alternatives, and merged edges are unique |

Saved findings intentionally keep one representative shortest chain per identity/selected analysis target. A findings count is not a count of every possible route. Saved long paths and the older linear query API retain their representative-chain behavior for continuation compatibility. Custom path searches expose alternatives without changing the saved analysis policy version or invalidating existing snapshots.

The native viewer is the default product view. The optional legacy browser viewer's linear path endpoint remains a representative-chain interface. It is not claimed to have the native viewer's new all-shortest graph behavior.

## Verification

108 comparisons against an independent simple-path enumerator cover equal branches, parallel permissions, cycles, direction, filters, and identity paths. Another 648 waypoint comparisons compose the independently enumerated legs. The three direct/filtered/no-match expectations from BloodHound's `TestGraphQuery_GetAllShortestPaths` fixture are also reproduced locally. Budget and traversal-policy tests cover incomplete results, distribution groups, unresolved identifiers, and inventory edges. Native UI tests cover selectable alternatives, empty filters, target filters, edge-budget ordering, read-only hashes, and the existing 801-candidate/270-hop behavior. Waypoint cases include missing middle legs, repeated nodes, zero-hop legs, ambiguous names, cancellation, shared graph budgets, swapped waypoint order, and matching CLI output. Compact-window tests verify a readable canvas, unclipped actions, and a fully scrollable table at 1040×720. PowerShell 5.1 and 7 are checked separately. BloodHound's Go server was not executed; this is source-based behavioral comparison, not a certified full-engine equivalence test.

For waypoints, visit/search budgets apply separately to each leg, the viewer's time/cancellation budget covers the entire query, and the displayed edge cap applies to the merged graph. A missing or search-limited leg is identified by number and endpoints. Successful legs alone do not establish a complete ordered route. Revisited nodes and relationships are allowed across legs, just as when combining the reference's pairwise queries. The graph table lists unique relationships; the ordered leg explanation preserves the route sequence. The optional collector Path Explorer does not have waypoint inputs; use the native desktop viewer or CLI for this feature.

## Remaining features, in implementation order

1. **A shared-fixture differential runner.** Load the same normalized graph into a running BloodHound instance and BHTest; compare canonical source/kind/target edge sets for every query and filter. This is needed before calling any supported subset one-for-one. No BloodHound service was configured for this update.
2. **Reusable searches.** Ordered waypoints are now implemented in the native viewer and CLI. Saved query/filter presets and waypoint inputs in the collector's optional Path Explorer remain to be added.
3. **Remaining GPO analysis.** Computer scope candidates now use enforcement, blocked inheritance and supported Read/Apply filtering. User/site/loopback policy, WMI evaluation, full effective-token checks, SYSVOL permissions, actual settings and joint WriteGPLink prerequisites remain. Raw Contains/GPLink edges remain inventory rather than privilege transitions.
4. **Remaining ownership work.** Rule 5 now preserves raw evidence, OWNER RIGHTS and captured policy, derives supported/conditional ownership candidates, suppresses unsafe projections and preserves merge conflicts. Property-specific limited-owner routes, cross-domain administrator exceptions and full operation-specific server/token checks remain. See OWNERSHIP.md.
5. **Remaining AD CS paths.** Extend the supported imported direct-root ESC1 scope to intermediate chains and forest host resolution, then validate other ESC/GoldenCert prerequisites. CA/template inventory alone does not establish these paths.
6. **Trust, delegation, and hybrid derivations.** Add cross-domain identities/membership, trust direction/filtering, special identity prerequisites, unconstrained-delegation and synchronization evidence. Do not infer them from a domain link or one property alone.
7. **Endpoint/relay and LAPS synchronization.** Require collected local-group/session/logon/relay/service facts and the relevant directory state. This lab has no member endpoints, so live equivalence for these routes cannot be tested here.

Missing frontend traversal kinds: GPLink, CoerceToTGT, SyncLAPSPassword, GoldenCert, ADCSESC3, ADCSESC4, ADCSESC6a, ADCSESC6b, ADCSESC9a, ADCSESC9b, ADCSESC10a, ADCSESC10b, ADCSESC13, ADCSESC16, SyncedToADUser, CoerceAndRelayNTLMToSMB, CoerceAndRelayNTLMToADCS, CoerceAndRelayNTLMToADCSRPC, WriteOwnerLimitedRights, OwnsLimitedRights, ClaimSpecialIdentity, CoerceAndRelayNTLMToLDAP, CoerceAndRelayNTLMToLDAPS, HasTrustKeys, WriteAltSecurityIdentities, WritePublicInformation, ManageCA, ManageCertificates, ServerIs, Contains, DCFor, SameForestTrust, SpoofSIDHistory, AbuseTGTDelegation, ProtectAdminGroups.

## Lab state

This update makes no AD changes. Updating the path engine does not restore the temporary DA test grant or change the contents of existing scans. A missing grant remains a missing route in that snapshot. Effective access and exploitability still require validation.

Read-only checks of retained lab snapshots confirm that the historical positive snapshot still yields the five-hop bhr.entry-to-DA chain, while the newer snapshot without the final grant returns NoPath. Both index hashes were unchanged. These are saved-data checks, not a new live scan or a lab repair.
