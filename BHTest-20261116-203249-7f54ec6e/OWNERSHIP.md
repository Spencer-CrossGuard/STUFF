# Ownership assessments — path rules 5

Ownership and permission to change ownership are retained separately from derived control paths. The viewer shows **Supported**, **Conditional**, or **Suppressed** ownership assessments in relationship tables and explains the reason on selection. Supported means supported by the model, not verified Windows effective access. Counts in the coverage summary are relationship counts, not numbers of end-to-end paths or saved findings.

## Native collection and interpretation

The collector retains the owner SID, applicable OWNER RIGHTS presence, supported explicit OWNER RIGHTS WRITE_DAC, relevant WRITE_DAC denies, computer-derived classification and captured directory-policy mode. Inherit-only ACEs do not restrict the current object. Callback/unsupported ownership ACEs remain uncertain; an unsupported OWNER RIGHTS entry is not mistaken for absence of OWNER RIGHTS. Explicit non-ownership permissions such as a separately collected WriteDacl or GenericAll remain separate.

The directory policy is read once per scan with a bounded base-scope LDAP search for dSHeuristics on the Directory Service configuration object. No modify request, SACL access, token elevation, ownership change or privilege use is performed. Read failure or skipped configuration leaves the mode unknown and does not abort collection.

Model rules:

- Non-computer ownership can project when the supported descriptor context establishes the modeled owner WRITE_DAC route.
- OWNER RIGHTS presence without supported explicit WRITE_DAC prevents automatic full-control projection. Raw evidence remains available. Explicit OWNER RIGHTS WRITE_DAC can preserve a candidate route without relying on implicit owner rights.
- Relevant denies and unsupported ownership ACL context suppress automatic projection. This conservative choice is not proof that all actual access is denied.
- Computer-derived objects include managed service accounts. Captured blocking mode suppresses implicit-owner routes unless a supported current-owner exception is established through known same-domain DA/EA membership. Nested security groups are checked; distribution groups, unknown identities and unrelated-domain admin groups do not establish the exception. The WriteOwner projection under blocking mode is conservatively suppressed.
- Explicit non-blocking mode can support a candidate. Default/unreadable computer policy stays conditional because behavior can depend on the server and its version. Newer restricted mode is not turned into unrestricted control.
- Conflicting object or policy snapshots suppress the affected projections. Merges retain conflict markers and raw records, then recompute conclusions. Derived ownership rows cannot substitute for missing source evidence.

Raw OwnsRaw/WriteOwnerRaw and imported limited-rights labels remain inventory. Property-specific limited-owner techniques are not automatically promoted to full object control. Cross-domain Enterprise Admin exceptions, full effective-token evaluation, administrator privileges such as restore/take-ownership privileges, and operation-specific newer-server restrictions are not completely modeled.

## Imports and older collections

SharpHound OWNER RIGHTS flags, managed-service-account classification and available domain dSHeuristics are preserved. A flag saying OWNER RIGHTS is present is not treated as an explicit WRITE_DAC grant. Missing ownership flags are not guessed to mean unrestricted ownership.

Older canonical ownership rows remain **conditional legacy relationships** when their new context is unavailable. Newly captured blocking/restricted or conflicting domain policy can suppress a legacy computer projection. These conditional rows do not establish verified control. Recollect with the current native scanner for the new per-object context.

Path rule version 5 requires matching scanner/viewer builds and regenerated saved analysis. Restart old windows and generate a new report folder. Existing scans can still be analyzed, with their missing ownership context stated explicitly.

The retained real lab scan was rebuilt at `C:\Users\Administrator\Documents\Codex\BH-Ownership-validation\current-report-v5`. It has one saved replication finding and 1,600 conditional legacy ownership relationships. No new live ownership scan or AD change was performed. Synthetic ownership fixtures are kept separately and are not selected as the latest lab report.

## Validation and reference differences

67 checks cover OWNER RIGHTS, inherit-only/callback entries, explicit WRITE_DAC, policy-character mapping, managed service accounts, nested administrator boundaries, conflicting snapshots, merge recomputation, legacy handling, real LDAP-entry serialization and SharpHound import. Viewer checks cover all three assessments, suppressed graph exclusion, inventory inspection, explanation isolation and unchanged index hashes. PowerShell 5.1 and 7 are checked separately, alongside the existing GPO, pathing, replication and scheduling tests.

The local BloodHound `packages/go/analysis/ad/owns.go` was reviewed. Its raw/postprocessed ownership split informed the design. Its fallback that treats unavailable policy as unblocked is not claimed to verify current server behavior. Our explicit conditional state, narrower administrator exception and conservative treatment of limited rights are deliberate differences. Full one-for-one equivalence is not claimed; BloodHound's server was not executed for this increment.

Microsoft references:

- [OWNER RIGHTS SID semantics](https://learn.microsoft.com/en-nz/windows-server/identity/ad-ds/manage/understand-security-identifiers)
- [Blocking implicit owner rights, including newer-server behavior](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/fb7c101d-ec8b-4fbf-bca8-7d7c2d747d0c)
- [dSHeuristics character definitions](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/e5899be4-862e-496f-9a38-33950617d2c5)
- [Standard access rights](https://learn.microsoft.com/en-us/windows/win32/secauthz/standard-access-rights)
