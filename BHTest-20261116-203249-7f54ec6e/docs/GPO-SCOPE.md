# Computer GPO scope candidates — path rules 4

This GPO model was introduced in rule 4 and remains available in the current rule-5 ownership release. Use matching current scanner/viewer builds; see OWNERSHIP.md for the newer ownership assessments.

This increment adds a conservative `GPOAppliesTo` relationship from a collected GPO to an eligible computer. A supported GPO-edit relationship can then connect a source to that computer for review. **This is a scope candidate, not proof that a policy executed or that the source has administrative access.** SYSVOL edit access, policy contents, client processing and resultant policy are not verified.

## Collection and derivation

The native LDAP collector now retains GPO flags, functionality version, WMI-filter reference, file-system path, version number, container `gPOptions`, actual OU/domain/container classification, and enabled/enforced link options. Disabled links remain observations. It requests the same Owner/DACL security-descriptor portions as before; it does not request SACLs, fetch passwords, follow SYSVOL paths, or contact member endpoints.

Computer scope requires a known enabled computer account, GPO functionality version 2, an enabled computer-policy portion, an enabled link to an OU/domain, and one DN-consistent containment parent per descendant. Non-enforced links stop at a descendant OU that blocks inheritance. Enforcement crosses that boundary. A GPO linked directly to a blocking OU is still considered for that OU's descendants.

The ACL summary requires a complete generic-read mask and Apply Group Policy grants. Supported simple allows can combine for the same trustee; Read and Apply may come through different collected security groups. Membership expansion uses only known security-enabled groups, plus the computer SID and implicit Everyone/Authenticated Users identities. Relevant denies or unsupported/conditional ACEs stop the entire GPO inference rather than approximating a full Windows effective-token check. This can produce false negatives. SID history, special token claims and partial read masks split across different trustees are not modeled.

Missing or conflicting metadata, ambiguous containment, unknown group types and unevaluated WMI filters prevent the corresponding inference. User policy, site/subnet scope, loopback, actual settings precedence and multi-prerequisite WriteGPLink abuse are not derived. A plain AD container is not accepted as a GPO link root, although a domain policy's scope can descend through such a container.

LDAP absence of `gPOptions` uses the protocol default of 0. Attribute-level read restrictions can make an absent attribute indistinguishable from a hidden one. Attribute visibility is therefore explicitly unverified. Recheck visibility and the live ACL before treating a scope candidate as effective application.

Scope and token work uses SQLite temporary tables, bounded depth and pair budgets. Reaching those budgets suppresses new GPO capabilities and records an incomplete coverage note. Scope/member witnesses, link enforcement and verification limits survive in each derived edge. Conflicting object/link markers survive merges and appear in retained records. Derived GPO edges are discarded on import and recomputed from source metadata, so stale exported edges cannot substitute for missing prerequisites.

## Reports and compatibility

Path rule version 4 reflects the new traversal rule. Restart old scanner/viewer windows. Older saved analyses must be regenerated in a new report folder. Existing collections can be reused for their previous relationship types, but **a fresh native scan is required to collect these new GPO fields**. Legacy and SharpHound imports without a complete supported GPO security summary do not acquire guessed GPO scope paths.

Raw Contains and GPLink inventory edges remain outside general privilege traversal. `GPOAppliesTo` is accepted only with our derivation evidence, a GPO source and a computer target. The viewer explains the containment chain and separate Read/Apply membership witnesses; HTML coverage notes explain skipped inference and limits.

The retained real lab data was rebuilt into `C:\Users\Administrator\Documents\Codex\BH-GPO-validation\current-report`. It has current rules and one existing replication candidate. Its original scan predates GPO metadata collection, so it correctly has no new GPO scope candidates. This is a report rebuild, not a new live scan. The synthetic GPO report used for UI checks is kept separately and is never selected as the latest lab report. AD was unchanged.

## Validation and comparison

Tests cover default Read/Apply grants, broad permissions, denies, enabled/disabled/enforced links, block boundaries, plain containers, nested split permissions, group cycles, distribution/unknown groups, WMI/flag/functionality stops, disabled computers, missing data, ambiguous parents, conflicting snapshots, merge recomputation, traversal guards, real LDAP-entry serialization and report compatibility. Native viewer checks verify scope witnesses, clear verification limits, relationship filters and unchanged snapshot hashes. Both PowerShell 5.1 and 7 are used. These are fixtures and saved-data checks; no live endpoint, SYSVOL/RSoP validation or new ordinary-user GPO scan was performed for this increment.

The local BloodHound `packages/go/analysis/ad/queries.go` and `filters.go` were reviewed for affected-object traversal, enforcement and inheritance behavior. Its schema declares `GPOAppliesTo`; the reviewed code also uses GPLink/Contains traversal for GPO scope queries. Tessera's explicit scope projection is not claimed to be a port of a complete BloodHound postprocessing pipeline. Full one-for-one parity remains unverified.

Microsoft references used for the implementation:

- [Group Policy scope and security filtering](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-policy/group-policy-scope)
- [GPO filter evaluation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gpol/5ead679e-5ec1-4f43-b927-1a339da316c7)
- [GPO flags and configuration](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gpod/d360d288-d7d5-49a9-83be-603805da1379)
- [Apply Group Policy right](https://learn.microsoft.com/en-us/windows/win32/adschema/r-apply-group-policy)
- [Group Policy container class](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adsc/f6adba54-f9c0-4045-b92c-a8b35782d6f5)
