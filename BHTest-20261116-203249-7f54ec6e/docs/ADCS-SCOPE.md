## Optional group enrichment and prerequisite diagnostics

SharpHound imports can now explicitly supplement unknown group state with bounded read-only LDAP groupType searches on the selected DC. Exact binary SID, returned identity, group class/flags and domain checks are required. Known imported values are preserved; failures, ambiguity, foreign domain and unsupported data remain unknown. Source/DC/object identity, read time and object change markers survive merges.

The unchanged reference fixture remains at 1 of 3 stored ESC1 endpoint pairs by design. With controlled LDAP replies classifying the fixture groups as security-enabled, the new import workflow reproduces 3 of 3. These are conditional mixed-time candidates: the export grants and memberships were not revalidated. All ESC1 projections in a dataset with enriched group identities carry that conservative qualification, even when a particular representative witness does not depend on an enriched group. This is neither a live BloodHound comparison nor live validation of those fixture group types.

Recorded prerequisite gaps now identify unestablished CA/template combinations and relevant unknown groups. Report, native object details and collection/path-limits help expose those reasons with labelled preview bounds. The diagnostics do not enumerate every possible composition or establish a universal cause for an arbitrary missing path. Path rules remain 6; existing collection files and AD are unchanged. The retained lab report still has zero ESC1 projections and now explains its missing CA evidence. See README.md for usage and the unavailable non-admin live-read validation result.

# AD CS / ESC1 scope — path rules 6

Tessera now derives `ADCSESC1` candidates from complete **imported SharpHound direct-root CA evidence**. Native LDAP-only template/CA inventory cannot supply these prerequisites and does not produce ESC1 edges. Full BloodHound parity is not claimed.

## Required evidence

- Explicit imported template metadata: requester supplies subject, authentication enabled, no manager approval, schema 1–4, and a collected signature count. Schemas above 1 require zero signatures. Missing properties are unknown.
- A recorded template publication to the exact CA being assessed.
- Template enrollment via Enroll, GenericAll or AllExtendedRights, and **CA-service Enroll** from a successful `CARegistryData.CASecurity` result. Directory-object Enroll does not substitute for the service result. Failed, contradictory, malformed or missing service collection does not qualify.
- Both branches reach the same known principal through recorded security-enabled memberships. Distribution/unknown groups cannot extend a token. Everyone/Authenticated Users are implicit only for known enabled accounts in the publishing object's captured domain. Domain-name-prefixed SharpHound well-known IDs are supported.
- The CA's single-entry chain and fingerprint match a collected RootCA single-entry chain/fingerprint. An NTAuthStore contains that same fingerprint, and root and NTAuth records identify the same collected target domain. Fingerprints must be well-formed SHA-1 identifiers. This models collected directory trust, not cryptographic signature, expiration or revocation validation.
- The exact CA's HostingComputer resolves to a collected computer with an explicitly enabled state and a domain matching the CA's captured domain.

Imported CA service ACE summaries, collection status/failure, publication, host, certificate chains, thumbprints and domain associations are retained in node records and survive compressed merges. Full registry blobs and certificates are not retrieved by Tessera. Relevant reported deny counts or non-allow ACE context suppress inference; summaries still do not establish full-token effective access.

## Path and viewer behavior

Derived relationships use a distinct provenance marker and are regenerated from source facts. Supplied ADCSESC1 edges are discarded during import, including those claiming the expected marker. Conflicting prerequisite snapshots suppress inference, and a conflict marker survives merge round trips.

Each principal-to-domain edge retains one deterministic CA/template/trust combination and separate membership witnesses (up to 64 objects each). Alternate qualifying CA/template compositions are not all expanded into the graph. Direct-root candidates appear in path search, saved findings, the relationship filter and both viewer explanation systems. The visible assessment is **Candidate**, with validation and remediation guidance; no certificate was issued or authentication attempted. Membership work is disk-backed and capped at 250,000 seed attempts and closure rows / 1,000 levels; exceeding the bound suppresses all ESC1 projections and is recorded in coverage.

Path rules changed to **6**. Scanner and standalone viewer are rebuilt together. Old saved analyses must be regenerated from retained collection data; a new AD scan is not required just to update the rules. Existing data cannot acquire missing service or template facts retroactively. Both installed copies' Open latest entries point to the refreshed retained lab report.

## Template review

The HTML section separately reviews template configuration: conditions matched (domain path unverified), not matched, metadata incomplete, or conflicting supplied snapshots. Counts cover all supplied distinct template IDs; the visible table is a clearly marked first-100 preview. A matched template is not a confirmed vulnerability. Review expands on request to keep the initial report concise.

LDAP collection retains name/enrollment flags, EKUs, application policies, schema and signatures. Bit 0x1 supplies the subject; the separate SAN-only flag is insufficient. Approval uses bit 0x2. Native authentication assessment recognizes Client Authentication, Smart Card Logon, PKINIT Client Authentication and Any Purpose. Empty EKUs or nonempty application-policy interactions remain unknown in this first native assessment. Imported explicit SharpHound authentication flags are preserved. Source CSV exports remain inventory exports, not full assessment exports.

Duplicate original report inputs with different template prerequisites are marked conflicting. Previously merged old datasets may already have lost conflicting records; use original consistent scan folders when reviewing that uncertainty.

## Comparison and validation

Reference source: local `BloodHound-main/packages/go/analysis/ad/esc1.go`, `esc_shared.go`, `adcscache.go`, plus `packages/go/ein/ad.go` and the SharpHound EnterpriseCA processor. The reference intersects template and CA enrollment on the same CA and combines root and NTAuth trust with exact-host qualification. Tessera intentionally supports a narrower scope.

`tests/AdcsPaths.ps1` validates positive, nested/cyclic and split-membership paths; missing/malformed service data; directory/service separation; different CAs; host and trust mismatches; unsupported intermediate chains; template conditions; deny context; forged projections; saved-finding evidence; and compressed/conflicting merge round trips. `AdcsViewer.ps1` checks native path display, evidence and snapshot immutability; `Adcs.ps1` covers template review; report helper tests cover explanations and safe text.

The unchanged local BloodHound Version6ADCSJSON fixture imports successfully and reproduces **1 of its 3 stored ESC1 endpoint pairs**. The other two rely on group membership branches whose group security-enabled state is absent from that fixture; Tessera deliberately requires that state. This is an import/stored-expectation comparison, not a live execution of BloodHound. Synthetic fixtures separately validate known security-enabled group paths.

The retained lab scan has 33 templates (one configuration match, two incomplete), but lacks imported CA-service prerequisites: **zero ESC1 projections**. Its existing DCSync candidate remains separate. No AD changes, CA registry probing, certificate enrollment or exploitation were performed.

## Remaining work

Intermediate chains and basic-constraint/path-length handling, cross-domain CA hosts/forest resolution, native effective enrollment ACL modeling, all alternate derivation compositions, and other ESC/GoldenCert rules remain unsupported. Current server authentication enforcement and real certificate issuance are unverified even for a modeled candidate. Complete BloodHound compatibility is still development work.