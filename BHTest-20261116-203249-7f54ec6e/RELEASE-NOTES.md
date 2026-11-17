Group import update: optional SID-verified read-only LDAP groupType enrichment, preserved lookup provenance, explicit unknown/failure/limit handling, conditional mixed-time ESC1 annotations and diagnostic reasons in reports/object details. Controlled LDAP replies reproduce all three stored reference endpoint pairs; unchanged exports still reproduce one. No AD changes or administrative fallback. Path rules remain 6.

Help update: shared offline catalog, searchable collector/viewer guide, contextual F1/property explanations, separate relationship descriptions and target-specific validation/remediation. The catalog contains 113 entries and covers 160 property names; help-only launch needs no snapshot. Native/HTML text handling, read-only index behavior and minimum-size layout are tested. Path rules remain 6.

Path rules 6 add ESC1 candidates for complete imported direct-root CA evidence, retaining separate template/CA-service enrollment, trust and host witnesses. LDAP-only inventory and unsupported cases remain excluded. See [ADCS-SCOPE.md](ADCS-SCOPE.md) for required evidence, tests and limits.

# Consolidated update — November 16, 2026

Path rules 5 separate raw ownership evidence from modeled control. Native OWNER RIGHTS and captured directory-policy context are retained; supported, conditional and suppressed assessments appear in the viewer. Explicit owner WRITE_DAC can preserve valid candidates; blocking policy, uncertainty and conflicts prevent unsupported projections. Imports preserve restrictions and merges recompute conclusions. Older collections remain visibly conditional where context is missing. See OWNERSHIP.md for tests, server-version limits and deliberate differences from BloodHound.

Path rules 4 add conservative computer GPO scope candidates, with new LDAP metadata, enabled/enforced links, blocked-inheritance handling, Read/Apply security-group witnesses, conflict-safe merges and coverage notes. Raw containment/link edges are not made privilege paths. WMI filters, relevant denies, missing/ambiguous metadata and unsupported ACLs stop inference. SYSVOL, policy content and resultant policy remain unverified. See GPO-SCOPE.md. A fresh scan is needed for the new GPO attributes; older analyses must be regenerated with the matching scanner/viewer.

The next parity increment adds two optional waypoints in the native viewer and CLI `-ViaIds`. It merges all shortest alternatives per ordered leg, explains failed/limited legs, deduplicates repeated relationships, and caps the combined graph. Swapping ends reverses the waypoint order. Compact windows keep a readable canvas and scroll the path workspace instead of clipping controls; reciprocal arrows retain minimum spacing. The CLI now validates its actual 250,000-node visit ceiling and exposes `-MaxPathEdges` (1–10,000; default 2,000).

The pathing follow-up compares the local BloodHound analyzer source and fixes first-path-only pair queries, native relationship filters, shortest-target edge-budget ordering, and duplicate graph edges. Both native graph queries and the collector Path Explorer now retain all equally short alternatives. See PATH-PARITY.md for verified behavior, remaining features, and the explicit limits on one-for-one compatibility.

The previous November 15 package was missing several collector changes from Claude's November 13 checklist, despite containing newer viewer and DC-picker work. The working source and the user's extracted package shared those omissions. File dates did not establish feature inclusion.

Reference: [Claude branch at bf337133](https://github.com/Spencer-CrossGuard/STUFF/tree/bf337133bee199c681aa8d6ff7b3e4994eb923d6/BHTest-Collector-Fixed).

Integrated functional changes from `008aba7`, `91764c0`, `3a5387f`, `890d8d6` and `d18854b`: GenericWrite mapping, delegation host aliases, default ACL noise filtering, missing native ACL labels, ACL trace helper, forest name lookup and its command-line action, clear missing-index handling, detailed progress/timing, correct scan-bar fractions, and direct reports over multiple inputs. Existing mappings and newer native APIs were preserved rather than overwritten. The standalone viewer is rebuilt from the combined source, unlike the reference checklist's older embedded viewer.

Two deliberate corrections: gMSA password-reader inference uses the embedded msDS-GroupMSAMembership security descriptor instead of a normal account ACL alone; ambiguous SPN hosts do not resolve arbitrarily. Host candidate tracking uses SQLite temporary storage to preserve bounded-memory indexing. Native source fingerprints prevent a previous assembly from silently surviving an update in the same PowerShell process.

The optional Create-OGCollectionFiles placeholder generator is not a runtime fix and is not included. It creates blank files, not an updated application.

Graph fixes from the user's screenshots are included: every selected permission is shown in a dedicated strip, curves use numbered markers instead of overlapping permission boxes, reverse directions remain separate, and All connections uses a spatial spread with selected-connection focus. No relationships are removed from the underlying index by these presentation choices.

Coverage limits remain: no live member endpoints, no password extraction, no live cross-domain forest in this lab, no complete AD CS/trust derivation, and no foreign-security-principal membership collector. Forest fallback is tested with mock directory responses and the local GC is checked separately. Read-only collection does not require administrative AD rights; fixture seeding/repair is a separate administrator operation.

The lab's protected Domain Admins ACL had lost the final tracked membership-write grant. The users were present in the failing snapshot. Restoring that one lab grant restored the previously validated DA chains; do not confuse this lab-state dependency with collector capability. AD can remove the grant again. No AdminSDHolder or privileged-group membership changes are made by the application.
