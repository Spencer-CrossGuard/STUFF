## Optional read-only group classification during SharpHound import

Enable **Read missing group types (import)** on the Report tab to supplement unknown imported group classifications. It uses the DC and identity on the connection tab. The option defaults off; ordinary offline imports make no LDAP connections. Known true/false classifications are preserved, including known observations in duplicate records. Malformed source Booleans remain unknown, with their original value retained for inspection.

```powershell
.\Start-Tessera.ps1 -ImportSharpHound -SharpHoundPaths 'C:\exports\collection.zip' `
  -EnrichGroupTypes -Server 'dc01.corp.local' -ImportOutputRoot 'C:\BHData\scans'
```

Use `-Credential` for a different ordinary domain reader if required. The implementation performs LDAP searches only, with signing/sealing and the existing bounded connection helper. It does not need the AD PowerShell module, change AD, collect security descriptors, or fall back to administrative credentials. Unknown groups are matched by an escaped binary objectSid filter; returned class, exact SID, single-valued groupType, objectGUID and selected domain identity must qualify. Foreign/non-domain SIDs, failed/ambiguous reads, missing identities and unsupported flag values remain unknown.

The default work limits are **5,000 selected unknown group IDs** and **60 seconds**, configurable with `-MaxGroupLookups` (1–25,000) and `-EnrichmentSeconds` (1–600). Preflight checks prevent a later known duplicate from being overwritten. Work stops starting lookups after the budget; an in-flight bounded operation and the final staging-file commit can finish later. All changes are confined to the new unpublished import dataset. Original exports and existing scans/reports remain unchanged. An enrichment failure leaves an otherwise valid offline import available and is explicit in progress, import completion status, metadata and report coverage.

Resolved records retain the groupType, security flag, source, DC, observed SID/GUID/DN, client read time, and object uSNChanged/whenChanged. Unknown records retain a result code where a lookup was selected. These timestamps/change markers are provenance, not groupType-specific history or proof of replication convergence. The `GroupTypeEnrichment` object in scan metadata records counts, limits and status. No credentials are stored in this metadata.

Classification is read **after export**; original permissions and memberships are not revalidated. ESC1 projections in a dataset containing enriched group identities are conservatively labelled **Conditional** (including otherwise independent representative witnesses). Their explanations and exported review plans retain that qualification. Other derived-rule interpretation must also consider the mixed collection windows. Reimport the original export with the option enabled and generate a new report; existing datasets are not silently repaired.

Reports now include **Unestablished path prerequisites** under AD CS. Native object inspection shows related CA/template and unknown-group reasons, and the collection/path-limits link includes a diagnostic preview. Examples distinguish missing CA-service permissions, certificate/host/trust problems, template requirements, unestablished enrollment intersections, unknown group types, conflicts and work limits. These are limitations of supported candidate combinations, not exhaustive proof of why an arbitrary source/target query has no route. Valid and suppressed combinations can coexist. The HTML preview shows up to 200 diagnostic rows; native object inspection shows up to 50 and the coverage panel up to 30.

Validation reproduces **3 of 3 stored BloodHound reference ESC1 endpoint pairs with controlled LDAP replies**, versus 1 of 3 without enrichment. This is not a live BloodHound execution or proof of real directory group types. Failure/identity/distribution/limit/merge tests and native/HTML diagnostics are covered. A live attempt as `CORP\CodexSandboxOffline` confirmed the unavailable-read fallback; no successful live enrichment could be established with that non-admin identity, and no privileged fallback or AD changes were used. Path rules remain **6**; scanner/viewer are packaged together.

The flag interpretation follows [Microsoft groupType documentation](https://learn.microsoft.com/en-us/windows/win32/adschema/a-grouptype). See ADCS-SCOPE.md for the remaining certificate-path limits.

## Help and explanations

Use **Help & glossary** in the collector header or native viewer. It works offline and can open before a report exists. The guide has **113 searchable entries**: 50 relationship explanations and 63 object, property and workflow topics covering 160 property names and 53 named viewer controls. Every explicitly named property emitted by the native collector and SharpHound adapter has a help entry; unfamiliar fields retain an explicit interpretation-unavailable fallback.

In the native viewer, **F1** opens help for the focused control. In Collected properties, select a field name and press F1, or choose **Explain a property**. Object inspection supplies type-specific interpretation and limitations. The help window supports multi-word search, Ctrl+F and Escape. It reads no AD data and needs no graph index when opened on its own:

```powershell
powershell.exe -NoProfile -STA -File .\Tessera.Viewer.ps1 -Help
```

New HTML reports embed the same authored catalog. Search tasks, property names or relationships in **Help & glossary**; property names in the evidence table are explain buttons with a return to the inspected object. Search explicitly labels its first-16 result preview.

Guidance separates meaning, source evidence, prerequisites, operational impact, proposed remediation, validation, rollback and limits. AddSelf/AddMember, Owns/WriteOwner, remote-management protocols, individual replication prerequisites, LAPS/gMSA/MSA access, enrollment and CA roles now have distinct explanations. Contextual guidance also covers account/group/device management, delegation, credential attributes and PKI objects. An explanation does not enable an unsupported relationship or prove successful exploitation. PATH-PARITY.md and the derivation scope notes remain authoritative about supported behavior.

The structure was compared with the local BloodHound help components and its official edge reference. The text is authored for Tessera's actual evidence, target-specific validation, operational impact and support limits. Help is provided on demand so that the graph remains uncluttered. No analysis/path rules or collection permissions changed; path rules remain 6. Reopen older windows for the new interface. Regenerate an old HTML report to embed the guide; a new AD scan is unnecessary for this text update.

Path rules 6 add ESC1 candidates for complete imported direct-root CA evidence, retaining separate template/CA-service enrollment, trust and host witnesses. LDAP-only inventory and unsupported cases remain excluded. See [ADCS-SCOPE.md](ADCS-SCOPE.md) for required evidence, tests and limits.

# Tessera

Tessera collects Active Directory data over LDAP and produces a local graph index and an offline HTML report. It runs on Windows with Windows PowerShell 5.1 or PowerShell 7. The embedded C# compiles at startup, and the graph uses the SQLite library supplied by Windows. There is no separate database server or PowerShell module to install.

The primary workflow is **Scan → Report → Path Explorer**. Tessera's scanner connects directly to your domain controller using your Windows account or the credentials you supply. SharpHound export import is an optional compatibility feature.

Reports normally review recognized privileged groups and collected domains. `-Report -TargetIds @('S-1-5-21-...-1555')` additionally reviews explicitly selected collected objects, such as an ordinary lab role. An explicit target is not automatically a Domain Admins or other privileged identity. Target IDs are validated, deduplicated without case sensitivity, and checked when resuming or comparing analysis. Lab fixture setup and full-domain DA/replication scenarios are supplied separately in Set-BHResultsLab.ps1 / Set-BHAdvancedLab.ps1; collection wrappers require ordinary LDAP credentials and a non-elevated process.

The native collector recognizes validated self-membership grants as `AddSelf` on groups, separately from `AddMember`. The parser applies its same-trustee deny checks; a recorded grant still requires effective-access validation. All path surfaces use the same supported relationship/bucket rules, and supported label casing is normalized. Unknown labels and inventory observations remain visible in object relationships but cannot become a supported attack path simply by being in an ACL file.

Path rule version 5 adds ownership assessments and retains the rule-4 computer GPO scope model. Older saved analyses/checkpoints must be regenerated with matching scanner/viewer builds. Regenerate reports in a new output folder using existing collected data for previous relationships. A fresh native scan is required for new GPO and ownership metadata. See [OWNERSHIP.md](OWNERSHIP.md) for supported, conditional and suppressed ownership relationships. See [GPO-SCOPE.md](GPO-SCOPE.md) for filtering, evidence and limitations; SYSVOL and actual policy processing are not verified. Older baseline snapshots can still be compared: their bounded path analysis is recalculated with current rules without changing the baseline file. Restart running collectors/viewers after updating.

DCSync analysis intersects `GetChanges` and `GetChangesAll` on the same domain through recorded memberships in groups explicitly marked security-enabled. The individual grants stay in inventory. Inferred DCSync relationships include both grant trustees and bounded membership witnesses, displayed in report/viewer explanations. A common qualifying group remains the path boundary rather than creating redundant edges for all its members. Native replication-related denies or unsupported applicable domain ACEs stop new membership-based inference for that domain. This is a conservative candidate model: it does not prove effective access, infer implicit Everyone/Authenticated Users membership, or use control edges as token membership. Missing group-type information prevents this inference through that group. Merges recompute inferred capabilities from the supplied grants and memberships instead of retaining stale derived edges. Existing directly collected/imported DCSync observations remain separate.

Run `.\Start-Tessera.ps1` (or right-click it and choose Run with PowerShell) to open the scanner. Folder layout: the root holds only `Start-Tessera.ps1` and `Tessera.Viewer.ps1`; `src` is the application code, `tools` has the Entra collector (`Collect-Entra.ps1`), `Trace-ObjectAcl.ps1`, `Catalog-BHOutputs.ps1` and `Open-Latest-Report.cmd`, `docs` has the documentation, and scans and reports are written to `BHData`. Run `Package-Tessera.ps1` to create a clean ZIP containing the application and source assets; saved scans, credentials, lab scripts and GUI settings are excluded. Extract the whole package before launching it.

At GUI startup, the current Windows user's `LOGONSERVER` is checked in the background using signed/sealed, read-only LDAP. A reachable DC in the current logon domain is selected automatically. If unavailable, the field stays blank and the GUI remains open; saved settings do not silently select a different server. Discovery never replaces a server the user has already typed.

The server field suggests matching DC DNS names while you type. A paged directory query lists all DC computer objects in the current user's domain, including RODCs. Names are saved in `BHData\dc-cache.json` (or the selected DataRoot), scoped to that logon domain; no passwords are cached. Cached names load immediately and refresh in the background. If the logon DC is unavailable, it is omitted from suggestions; other cached names remain available and can be used to refresh the list without automatically selecting a replacement. Directory-listed or cached DC names do not guarantee current availability. Normal Connect still checks the server and credentials before loading OUs. Workgroup/offline use remains available with manual entry.

Double-click `Open-Latest-Report.cmd` or choose **Open latest in Viewer** on the scanner's Report tab. Latest selection checks completed runs and GUI reports by index modification time, excluding unfinished runs and reports being written. Custom GUI report output paths are remembered in your data folder. For another data folder: `Open-Latest-Report.cmd -DataRoot "C:\MyData"`. The standalone viewer also accepts `-Latest -DataRoot ...`, or `-IndexPath` pointing at either report.html or graph.sqlite.

In the native viewer's Paths & graph tab, enter an object name or ID; `DA` selects Domain Admins. Multiple matching objects/domains require an exact ID. With a source, **Find path** returns one shortest recorded route. Leave source blank or choose **Routes to target** for a target overview. The visible **Shortest routes** default keeps edges toward the target on shortest routes, including equal-length alternatives, while omitting cyclic and longer detours. Choose **All connections** to retain the full reverse-reachable subgraph. This is the supported collected graph, not every repeated cyclic path permutation or proof of effective access. Discovery has a 2,000-relationship budget before view filtering, the selected visit budget and a 10-second deadline; limits remain explicit even if the chosen overview displays fewer relationships. Saved finding previews still retain one shortest chain per identity.

Parallel relationships are grouped by direction. Select a connection to show every permission in a separate strip above the graph, with matching numbered markers on its curves; labels are no longer silently suppressed or drawn in boxes across other connections. The table keeps the full evidence. **All relationships** restores the overview table and clears the selected connection. **All connections** uses a two-dimensional spread with no mass of inline permission labels; selecting a connection emphasizes it and dims the surrounding edges. Existing privileged access remains context, not automatically a new escalation finding.

## Collector updates integrated November 16, 2026

This version selectively integrates the functional changes from `Spencer-CrossGuard/STUFF`, branch `claude/eloquent-fermi-o0xkoc` at `bf337133bee199c681aa8d6ff7b3e4994eb923d6`. It preserves the newer bounded scheduler, page processing, ranged/forward group membership, split replication inference, attribute ACL checks, standalone viewer and DC suggestions. See `RELEASE-NOTES.md` for provenance and validation. Do not replace individual files with that older branch: its native APIs differ.

The GUI title shows the installed package ID from BUILD-INFO.json. Release ZIPs now place the application files at the ZIP root, so normal extraction does not create two identical nested package folders. Preserve your existing BHData folder when updating an installation.

- All-properties WriteProperty grants now produce GenericWrite. Attribute-specific AddSelf, AddKeyCredentialLink and WriteSPN remain; WriteGPLink is collected on domains, OUs and sites. Default SYSTEM and Enterprise Domain Controllers ACL grants are filtered as noise.
- Constrained-delegation SPNs resolve through host aliases and computer DNS names, retaining legacy full-SPN aliases. Shared hosts mapping to multiple accounts remain unresolved rather than selecting an arbitrary account. Re-scan for newly serialized delegation targets; re-report to rebuild aliases.
- Native gMSA reader candidates come from the `msDS-GroupMSAMembership` descriptor on actual gMSAs, with explicit evidence and unverified effective access. Plain ReadProperty on the account's managed-password attribute is not enough. Applicable denies/unsupported membership ACEs stop native inference. The collector does not request `msDS-ManagedPassword` or retrieve passwords. LAPS remains import-only; no live gMSA endpoint or password-retrieval test is claimed.
- GUI collection enables external-name resolution. It searches the local domain and then port 3268 on the selected DC for forest names, excluding foreign-security-principal stand-ins. Non-GC/unreachable servers warn and continue with unresolved SIDs; trusted external forests are not searched. Existing scans can be named with `Start-Tessera.ps1 -ResolveNames -ScanPaths <folder> -Server <dc>`, then re-reported.
- Index loading and merge writing report file/step progress; GUI merge/report status includes step detail, elapsed/quiet time and recent output-file activity. The scan bar uses completed targets and containers. Automatic multi-OU reports read their scan folders directly without an intermediate merge. Counting and final commits may remain indeterminate.
- `Trace-ObjectAcl.ps1` provides read-only live/offline ACL diagnostics and a missing-index check before LDAP. Native source fingerprints now reject stale loaded assemblies after source changes. Close existing windows and launch a fresh process after updating.

New detection needs a new scan. UI, progress, diagnostics and alias changes can use existing datasets, but a report cannot invent a permission absent from its captured data. Lab grants on protected groups can be reset by AD protection; verify/reapply the tracked lab fixture before testing expected DA chains. The application does not disable AdminSDHolder or automatically create those grants.

The viewer shows the full opened index path, modification time and saved/current path-rule versions. An outdated warning concerns saved report analysis, not the age of the AD collection. Update scanner and viewer together, close older windows, and rebuild the report from the existing dataset when its rule version differs; another AD scan is unnecessary. A loaded viewer from a different build is now rejected instead of silently reused.

The desktop viewer has a collapsible details panel and a draggable divider between the graph and path steps. Findings show review priority beside their score. Object inspection supports Back/Forward history (up to 100 objects) and formatted property previews. Search fields support Enter and Ctrl+F; Escape cancels an active query. Use Swap ends to reverse the source/target fields before running a new path search. Long-path continuation keeps the original endpoints and cumulative step numbers, so later segments remain identifiable. These are desktop viewer features; the browser interface remains available separately.

Relationship guidance is tailored to the target object when its type is known. The report and desktop viewer share the same authored context entries for meanings, prerequisites, recommended changes, validation and change impact. Examples distinguish user property writes from password reset, directory computer-account control from endpoint administration, GPO directory rights from SYSVOL access, and time-sensitive session observations from reusable permissions. Inventory-only facts prescribe review rather than automatic removal. Exported review plans include these prerequisites and change-impact notes. This guidance was informed by the supplied BloodHound help structure and is independently written; it does not establish full BloodHound analytical parity.

The output catalog links directly to the latest completed run. The GUI keeps detailed collection/report diagnostics in the run log while showing stage progress and target summaries. Closing the GUI stops its active jobs and marks an unfinished collection cancelled. `-Gui -Credential $credential` can supply an existing PSCredential for LDAP; its password is never saved in GUI settings. Endpoint collection continues to use the process's Windows account and remains off by default.

For a simpler scan-and-report workflow, use `./Start-Tessera.ps1 -Run -BaseDn 'DC=corp,DC=contoso,DC=com' -Server 'DC1.corp.contoso.com' -Credential (Get-Credential)`. It creates one unique folder under `BHData/runs`, prints a short result, and saves detailed messages in that run's logs. Open `BHData/START-HERE.html` or `BHData/runs/INDEX.html` to find runs. Each run has its own START-HERE page, status manifest, `report/report.html`, `report/graph.sqlite`, `data` and `logs`; report/index files remain together. Failed or cancelled GUI runs receive a distinct status. Existing `-Scan`, `-Merge` and `-Report` commands retain their scripting outputs.

GUI scan batches use the same layout, including separate folders for multiple targets and automatic reports. Custom collection roots are respected. Older outputs are preserved in a collapsed catalog section; `./Catalog-BHOutputs.ps1` refreshes that catalog without moving or deleting data. Manually chosen report folders retain their existing behavior.

The supplied BloodHound analyzer's OWNER RIGHTS handling informed a conservative ownership safeguard: native ownership and WriteOwner constrained by applicable OWNER RIGHTS ACEs are inventory-only until limited owner privileges are evaluated. Import applies this safeguard when source owner-rights flags are present. Explicit ordinary-trustee rights remain observations subject to full-token access evaluation. This is not a port of BloodHound's full limited-rights post-processing; recollect/regenerate old reports and restart the collector/viewer to use the change.

## Portable full-index desktop viewer (no ports)

Run `./Tessera.Viewer.ps1` and choose a completed `graph.sqlite`, or supply its path:

```powershell
./Tessera.Viewer.ps1 -IndexPath './BHData/reports/latest/graph.sqlite'
```

Copy **only `Tessera.Viewer.ps1` and `graph.sqlite`** to another Windows computer to review the data. The default viewer uses native Windows Forms controls and an embedded C# graph canvas. It opens **zero listening ports**, makes no network requests, and needs no browser or WebView runtime. It needs Windows PowerShell 5.1 or PowerShell 7 and Windows' SQLite library. There is no shipped executable, installer, Node runtime, downloaded asset, database service, or additional PowerShell module. Normal script execution and `Add-Type` must be permitted by the host's policy; the launcher does not change execution policy.

The launcher opens a desktop window with **Saved findings**, **Objects & relationships**, **Paths & graph**, and a shared plain-language helper. Drag the graph to pan, use the mouse wheel or zoom buttons, select a relationship for its meaning and recommended fix, and use **Read path / Previous node / Next node** for readable navigation. Keyboard arrows pan, +/− zoom, and 0 fits the graph. Source and target buttons in object search populate the path query. Close the window to release the index. Queries run on a background worker; **Cancel query** stops work, and closing during a query cancels it before releasing the index.

The viewer pages through **every saved candidate and every collected object**, independently of the HTML report's 200-finding preview. It provides full-index name/ID searches, retained object properties, incoming/outgoing relationship pages, plain-language explanations, recommended fixes, and interactive path graphs. Saved paths are loaded in segments of 128 steps with **Continue this path**. On-demand shortest-path searches have no fixed hop cutoff, a selectable 50,000–250,000-identity visit budget, and a 10-second query deadline. A visit/time limit or cancellation is reported as incomplete, distinct from finding no supported path. Continuing an on-demand path searches from the last displayed identity toward the same target; a segment export is explicitly limited and is not a complete-path export.

The selected index is opened read-only and locked against changes for the session. Use a completed standalone snapshot, without a WAL or recovery journal. Generate another report in a different folder, or stop the viewer before replacing this index. Full properties are displayed up to 1 MiB decompressed / 2 MiB stored per record; oversized records receive an explicit notice. The original collection coverage is not retained in the index, so consult the original report and scan metadata alongside the viewer.

Desktop queries use one background worker and an 8 MiB SQLite cache. Candidate paging seeks through the saved ranking index. Name searches and outgoing-edge searches scan bounded disk windows because existing indexes have no name-search or source-edge indexes. The interface advances through empty windows automatically, then offers continuation; an empty partial window is never presented as a completed search. This keeps memory bounded, but a selective search on millions of records can still take time. The viewer retains one result page and one graph segment.

The previous localhost browser interface remains available **only when explicitly requested**:

```powershell
./Tessera.Viewer.ps1 -Browser -IndexPath './BHData/reports/latest/graph.sqlite'
```

Only browser mode binds an available `127.0.0.1` port and creates a private session token; it never binds a LAN interface. Its `-NoBrowser` and `-NoWindow` options require `-Browser`. The default desktop mode never constructs the HTTP host, requests firewall changes, or falls back to opening a port. Browser mode has four bounded worker slots. Click **Stop viewer** in the browser or close its control window to stop it.

Developers regenerate the standalone script with `./Build-Viewer.ps1` after changing its embedded sources. `./tests/Viewer.ps1` exercises the standalone copy with synthetic data, 801 findings over 17 pages, 802 objects, retained compressed properties, 270-hop paths and continuation, bounded queries, rejected unauthorized origins/hosts/sessions, unchanged index hashes, and clean shutdown. Pass `-PowerShellPath` to test another installed PowerShell version.

`./tests/Desktop.ps1 -IndexPath <synthetic-test-graph.sqlite>` tests the desktop controls against the fixture generated by `tests/Viewer.ps1 -Artifacts <directory>`. It checks full candidate paging, global search, path continuation, object evidence, relationship paging, graph rendering/zoom/navigation, shared explanations/fixes, cancellation and safe close, unchanged index hashes, and **zero IPv4/IPv6 TCP listeners belonging to the viewer process**. Use `-ScreenshotDirectory` to render the desktop window for layout review. Desktop and browser modes are checked on PowerShell 5.1 and 7.

## Run a scan

Launch the WinForms interface with `./Start-Tessera.ps1`, or use the CLI:

```powershell
./Start-Tessera.ps1 -Scan -BaseDn 'DC=corp,DC=local' -Server 'dc01.corp.local' -OutputRoot './BHData/scans'
```

Workers process one LDAP page at a time and write gzip-compressed NDJSON shards directly from C#. The collector divides work by immediate child container and sweeps the base and its remaining direct children. The scheduler keeps at most the configured number of worker pipelines alive. Start with the default of eight workers and a page size of 1,000. Measure elapsed time and domain-controller load before tuning either setting. `-UncompressedOutput` disables compression for scans and merges; both formats can be read together.

Collection passes each LDAP page directly to the native writer and wakes the scheduler when workers complete. This reduces per-object PowerShell overhead and idle time between small shards without raising worker/page defaults. In the small DC lab, five alternating before/after runs per version reduced median collection time from 1.41 to 0.79 seconds with identical output. These are warm-lab measurements, not a large-domain guarantee; see `../Tessera-AD-Lab/PERFORMANCE-VALIDATION.md`. Restart an already-open collector after updating the native sources.

Certificate authorities, templates, certificate stores, issuance policies, configuration containers, sites, site servers, subnets, and domain trust inventory are included by default. `-SkipConfiguration` omits the configuration-partition pass. A scoped OU scan still collects this shared inventory and domain trusts; its account and computer scope remains the selected OU. The legacy `ous` dataset bucket includes generic directory containers; retained `ObjectClasses` distinguishes their actual classes.

Native `ManageCA`/`ManageCertificates` records are unverified directory-ACL mask observations for collector comparison, not evidence of actual CA-service authority. They carry directory provenance and remain in the non-traversable inventory bucket. Policy OID/group links and site associations are also inventory only. `AllExtendedRights` on groups and other non-account objects is retained as inventory rather than assumed to grant identity or membership control; imports apply the same restriction. See `../Tessera-AD-Lab/DIRECTORY-ONLY-VALIDATION.md` for evidence and limits.

Configuration inventory uses policy OID aliases to resolve template associations across datasets. Generic directory containers, including Builtin, use `objectGUID` rather than a namespace SID as their identity. Recollect older datasets before combining them with new scans where those historical IDs differ.

Enable **Collect endpoint groups and logons using my Windows account** in the GUI for native endpoint collection, or use:

```powershell
./Start-Tessera.ps1 -Scan -BaseDn 'DC=corp,DC=local' -Server 'dc01.corp.local' -CollectEndpoints -EndpointThreads 4 -EndpointTimeoutSeconds 20
```

This phase contacts the collected computers using Windows APIs to enumerate local Administrators, Remote Desktop Users, Distributed COM Users, Remote Management Users, and logged-on users. It produces `AdminTo`, `CanRDP`, `ExecuteDCOM`, `CanPSRemote`, and `HasSession` relationships. It uses the Windows identity running Tessera; credentials supplied for LDAP do not change the endpoint identity. The default is four reusable helper processes and a 20-second deadline per host. An inaccessible or stalled host is recorded individually in `endpoint-status.ndjson.gz` and `endpoint-metadata.json`. A timeout discards that host's unfinished records. The report describes skipped or partial endpoint coverage.

A lost paging session restarts its shard with a fresh cookie and replaces that shard's output. Permanent LDAP failures, worker failures, and object processing errors make the scan incomplete. See `scan-metadata.json` and the worker logs for diagnostics. An incomplete or cancelled scan is excluded from merge and report. The optional CLI switch `-ResolveExternalNames` looks up names for out-of-scope ACL trustees. It adds LDAP and disk work and only changes display labels.

Collection reads both account `memberOf` backlinks and group `member` lists, including ranged values. Merge and indexing deduplicate the two views. This retains memberships when only the group's list is readable; unreadable links can still limit coverage. If `userAccountControl` is unreadable or invalid, the node records `AccountStateKnown: false` and null UAC, disabled-state and UAC delegation flags. `MissingAccountControl` and the scan's coverage notes describe this gap. Unknown state must not be interpreted as an enabled account or as absence of delegation.

Attribute-specific write-property ACEs produce `AddKeyCredentialLink` and `WriteSPN` on users/computers, and `AddAllowedToAct` on computers. The parser checks exact schema GUIDs and applies its same-trustee deny suppression. These labels describe recorded write permissions; they do not establish effective access across a full token, successful authentication, or configured delegation. `AllowedToAct` remains the separate relationship read from an existing delegation descriptor. Run `tests/AttributeAcls.ps1` for target-type, inheritance, deny and non-write-right regressions.

Write-property ACEs for the Account Restrictions property set (`4c164200-20c0-11d0-a768-00aa006e0529`) produce `WriteAccountRestrictions` on user/computer targets. Read-property, validated-write and control-access rights alone do not produce this label; existing broad grants retain their generic labels. Matching and broad same-trustee write denies suppress the observation. This records a property-set grant, not proof that every member attribute/account-control bit is writable or that a complete takeover path exists. See `../Tessera-AD-Lab/ACCOUNT-RESTRICTIONS-VALIDATION.md` for low-privilege live checks.

The GUI can run selected OUs sequentially. It skips a selected child when its selected ancestor covers it. If a target fails, the rest still run. Automatic reporting requires all selected targets to finish successfully. Completed targets can be reported on individually. The batch log is written under `BHData`.

## Merge and report

### Import a SharpHound export

On the **Report** tab, click **Import SharpHound**, select one or more ZIP or JSON exports, then click **Generate report**. The importer reads the archive entries directly and writes a dataset that can also be merged with native Tessera scans.

```powershell
./Start-Tessera.ps1 -ImportSharpHound -SharpHoundPaths './collection.zip'
```

The importer supports SharpHound JSON schemas 5 and 6. It maps selected ACLs, memberships, sessions, local group rights, and delegation to graph edges. Trust and certificate inventory is preserved where supported, and the report flags the limits of its analysis. ESC1 direct-root candidates are derived under the documented imported-evidence prerequisites; other AD CS and general trust derivations remain unsupported. The importer records skipped object types in the dataset and report.

Imported ACL edges preserve the export's allow observations. The raw SharpHound 2.18.0 lab export included an allow edge for an explicit-deny control; the importer cannot reconstruct deny evidence absent from that JSON. An imported path therefore does not establish effective access. Native and imported datasets also have different inventory scope and ACL-label conventions; compare object identities and specific relationships rather than aggregate edge totals.

New native scans identify GPOs by their AD `objectGUID`, matching SharpHound object IDs. Native GPO links retain the policy DN until merge/indexing resolves it. Older native scans used the GUID in the policy's name, which can differ from `objectGUID`: recollect those datasets before mixing them with SharpHound exports or new scans. Existing saved indexes are not rewritten, and cross-version snapshot comparison can otherwise show GPO identity changes.

A report accepts raw scan folders and merged datasets, and resolves DN-based edge endpoints and deduplicates relationships across all its inputs. Run a merge when you want a reusable NDJSON dataset:

```powershell
./Start-Tessera.ps1 -Merge -ScanPaths './BHData/scans/scan-a', './BHData/scans/scan-b' -MergeOutputRoot './BHData/merged'
./Start-Tessera.ps1 -Report -DatasetPaths './BHData/merged/merged-example' -ReportOutputDir './BHData/reports/latest'
```

- `report.html` is a portable dashboard with counts, ACL rankings, a name-search sample, and a bounded four-hop neighborhood around privileged group SIDs. Large datasets still produce a preview. The browser does not load the full graph.
- `graph.sqlite` stores the deduplicated nodes and edges. Open it in **Path Explorer** for full-dataset search and shortest paths.
- Add `-ExportCsv` or check **Export CSV** in the GUI to write the original NDJSON source rows as CSV under `assets`. Use the merged dataset as the input when you want deduplicated CSV rows.

```powershell
./Start-Tessera.ps1 -SearchName 'Domain Admins' -IndexPath './BHData/reports/latest/graph.sqlite'
./Start-Tessera.ps1 -PathQuery -SourceId 'S-1-5-21-10-20-30-1105' -TargetId 'S-1-5-21-10-20-30-512' -IndexPath './BHData/reports/latest/graph.sqlite'
```

In the GUI, open **Path Explorer** and choose **Use last report** or browse to a graph index. Search for the source and target by name or exact ID, select a result on each side, and choose **Find shortest path**. Use **Filter edges** to restrict which relationships the search can traverse. The CLI equivalent is `-EdgeTypes @('MemberOf', 'AdminTo', 'HasSession')`. The table shows each hop and its relationship. Hover over an endpoint to see its ID.

Path queries traverse supported ACL, membership, and access relationships in the supplied dataset. If a query reaches its visit limit, it returns `VisitLimitReached`. Increase `-MaxVisited` to search farther. Unresolved DN endpoints survive merges so another dataset can resolve them later. Use scans from the same collection window to avoid retaining permissions or memberships that changed between scans.

Keep scan folders, the graph database, CSV files, and the HTML report private. They contain directory data and privilege relationships.

### Understand the report

The report includes a review workspace with section navigation, searchable relationship rows, a graph preview with directional arrows, object inspection, and collection coverage. Select **Explain** beside a relationship for its meaning, the recorded evidence, potential impact, checks to perform, and limits of the observation. Select a graph node or an object search result to inspect its connections. **Explain a term** searches the built-in glossary using labels or phrases such as `full control`.

The helper works offline and is embedded in the HTML. It uses built-in explanations and supplied graph facts; it does not send directory data to an AI service. **Review first** labels permission or access capabilities that merit investigation. **Context** labels membership and inventory facts. These labels do not establish confirmed vulnerabilities or severity. Unknown relationship types receive an evidence-only explanation.

The graph supports mouse dragging, wheel zoom, touch panning, and two-finger pinch zoom. Visible **+ / −**, **Fit all**, and **Read path** controls are available on every screen size. Narrow path views use a vertical layout at readable zoom; **Previous / Next** and the node selector move through the chain. Tap a node, its label, or a relationship line to select it. On a phone, selection stays beside the graph; choose **Explain selection** to open the full helper. Scroll the page outside the canvas. With the canvas focused, arrow keys pan, +/− zoom, and 0 fits the view. Dense overview labels are suppressed when they would overlap. Regenerate and download the HTML again to update copies already saved on a phone.

The relationship review includes up to 40 rows per ACL, access, and inventory bucket from the full index, plus the bounded privileged-group neighborhood. Rows are sorted for presentation after sampling; the sample is not an exhaustive or globally ranked findings list. Filters search this HTML sample. Full-dataset object search and path queries remain in Path Explorer. Regenerate existing reports to use the new interface; their previously generated HTML stays unchanged.

### Findings, fixes, and snapshot comparisons

**Prioritized exposure candidates** are computed with a disk-backed reverse search from recognized privileged groups and all collected domains. The analysis keeps one shortest recorded chain per identity. It excludes pure membership chains, unresolved aliases, distribution-group membership traversal, and unknown relationship types. Alternative routes and collection gaps still need review. Discovery continues independently of the 200-candidate HTML preview. The HTML pages twelve embedded candidates at a time; use **Path Explorer → Findings** to page through every discovered candidate in SQLite.

Select **Explain path & fixes** for priority reasons, the complete recorded chain, and candidate points where a permission or access change could break that chain. Select **Explain this step** for a recommended change, the exact affected source and target, prerequisites, validation, and rollback guidance. **Export this review plan** saves those details as text. These recommendations require owner and dependency review; Tessera does not change directory permissions or endpoint configuration. A broken chain does not establish that every alternative route is gone.

Priority is a transparent triage score: a recognized target contributes 30, the strongest capability contributes 15–40, direct group-member breadth can add 5 or 15, and chains of one or two hops add 5. Missing source object records subtract 10. Scores of 65 or more receive high review priority. This ranks the discovered sample, rather than establishing global severity or exploitation probability. Effective access, legitimate service roles, disabled accounts, credential protections and collection coverage still need validation.

Object inspection embeds up to 500 detailed records, prioritizing finding steps and preview objects. Records over 16,384 characters remain in the SQLite index; long property values and lists are shortened for display. Collection metadata describes dataset windows, and merge processing timestamps are identified separately. To see an arbitrary retained object, open **Path Explorer**, search by name or ID, and double-click a result. Its properties and up to 100 related edges appear in a details window. The CLI equivalent is:

```powershell
./Start-Tessera.ps1 -InspectObject 'S-1-5-21-10-20-30-1105' -IndexPath './BHData/reports/latest/graph.sqlite'
```

For before-and-after review, check **Compare previous index** in the launcher's Report tab, then select the earlier report's `graph.sqlite` when generating. Choose a new output folder to preserve the baseline. Or run:

```powershell
./Start-Tessera.ps1 -Report -DatasetPaths './BHData/scans/after' -ReportOutputDir './BHData/reports/after' -BaselineIndexPath './BHData/reports/before/graph.sqlite'
```

Comparison counts cover the full indexes: added/removed object IDs and added/removed/unchanged relationships, including alias-resolution state. Properties changing on an existing object are not counted as new objects. Current candidate paths are checked against the baseline's recorded relationships, and bounded baseline candidates are checked against the current index. The report distinguishes **Newly observed path**, **Still observed**, and **No longer observed**. Missing evidence can reflect changed scope, permissions, method or collection time, so “no longer observed” does not mean “fixed.” Relationship samples are capped at 50 per direction. Snapshot comparison adds full-index read work and is optional.

### Quick and Deep analysis

On the Report tab, open **Analysis settings**. **Quick** defaults to eight hops and 50,000 identities. **Deep** defaults to no hop cap, no identity cap, and a ten-minute traversal budget. Each budget can be changed; zero removes that particular cap. Time budgets apply to traversal, rather than fingerprinting, indexing, comparison, export or report publication. Discovery never stops just because the HTML preview has 200 candidates.

Deep mode stores its FIFO frontier, predecessor links, ranking and per-node edge cursor in `analysis-checkpoint.sqlite` in the report output folder. Transactions checkpoint small batches. **Cancel** requests a cooperative traversal stop and produces a partial report with its stop reason. Preparation and optional snapshot comparison may finish first. Closing or killing a process can lose its uncommitted batch; SQLite rolls it back when the checkpoint is reopened for resume.

To continue, select **Deep** and **Resume saved analysis**, keep the same datasets and output folder, and raise any exhausted budget. Resuming skips index construction but verifies SHA-256 fingerprints of the dataset files and metadata. Changed inputs or targets are rejected. A fresh deep run refuses to overwrite an existing checkpoint; use a new output folder for a new snapshot. The graph checkpoint must stay paired with its original input files. The published `graph.sqlite` is a separate copy so the report remains readable while a later resume updates the checkpoint; allow disk space for both databases and temporary publication files.

The report displays **Completed**, **DepthLimit**, **VisitLimit**, **TimeLimit**, or **Cancelled**, with the stopping reason, visited/processed/pending counts in the native finding browser and CLI. Completed means the frontier was exhausted for supported relationships and the selected targets. It does not mean every alternative route, all BloodHound relationship types, or every effective-access prerequisite was evaluated.

```powershell
# Deep analysis: no hop or identity cap; ten-minute traversal budget by default.
./Start-Tessera.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/deep-a' -AnalysisMode Deep

# Continue the same checkpoint with a larger per-invocation time budget.
./Start-Tessera.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/deep-a' -AnalysisMode Deep -ResumeAnalysis -AnalysisSeconds 1800

# Explicit budgets; zero removes the selected cap.
./Start-Tessera.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/custom' -AnalysisMode Deep -AnalysisDepth 0 -AnalysisVisitLimit 2000000 -AnalysisSeconds 0
```

For CLI cancellation, create an `analysis.cancel` file in the output folder while traversal is running. Starting or resuming a report clears an old cancellation marker. Concurrent report writes to the same output folder are rejected with an exclusive process-held lock. Use separate output folders for concurrent runs. Resume keeps existing discoveries; lowering a budget does not remove candidates already found.

**Path Explorer → Findings** loads 100 rows per page from the complete discovery index. Its Next/Previous controls use score-and-sequence cursors, rather than loading the full list or repeatedly skipping earlier rows. The CLI equivalents are:

```powershell
./Start-Tessera.ps1 -FindingPage -IndexPath './BHData/reports/deep-a/graph.sqlite' -FindingPageSize 100
# For the next page, use the last row's Score and Sequence.
./Start-Tessera.ps1 -FindingPage -IndexPath './BHData/reports/deep-a/graph.sqlite' -AfterScore 70 -AfterSequence 501 -FindingPageSize 100
```

A finding's displayed path is limited to its first 256 steps independently of traversal depth. Such a finding has `PathLimited`, the actual `HopCount`, and `NextPathId`; its truncated preview is not used to assert that the complete path persisted or disappeared in a baseline. Continue its recorded chain with `-TraceFinding <NextPathId> -IndexPath <graph.sqlite>`, which returns up to 256 more steps per call. Direct shortest-path queries in Path Explorer retain their separate identity visit budget. The SQLite library supplied by Windows must support row-value comparisons (SQLite 3.15 or later).

## Large datasets

Collection and file import stream records; the index resolves aliases, deduplicates edges, and executes graph queries on disk. Reports retain the collected object properties in SQLite, with larger records compressed internally. Merged datasets are compressed by default. CSV export is optional because it adds another full read and potentially large output. Allow disk space for source data, the index, and a temporary index when rebuilding a report or merging.

Before the disk-backed deep-search engine was added, a local synthetic run with the bounded findings pipeline processed **3,000,004 objects and 12,000,013 relationships**. Native record writing took **22.22 seconds**, indexing, bounded exposure analysis and report generation **54.03 seconds**, and peak process memory was **316 MB**. Compressed scan files occupied **121.1 MB**, and the retained-property index **2,203.9 MB**. A known one-hop query took **1.57 ms**. This exercises the actual native writer and report pipeline with generated directory entries; it excludes LDAP/network latency, endpoint collection and optional snapshot comparison. Real schemas, property sizes, graph shape, hardware, and remote response times affect these figures.

## Coverage and validation

A synthetic deep-search run processed **1,000,004 objects and 4,000,014 relationships**, visited **1,000,005 identities** (including referenced outside identities), and discovered **1,000,002 candidates**. Complete indexing, traversal and reporting took **170.63 seconds**, including **149.2 seconds** of traversal. Peak process memory was **341.9 MB**; the index including search state occupied **927.6 MB**. Retrieving 100 finding records near the 900,000th result took **10.21 ms**. The HTML embedded 200 candidates while the full list remained in SQLite. This intentionally broad synthetic graph differs from the earlier bounded benchmark and excludes live LDAP/endpoint latency and snapshot comparison.

The collector records domains, OUs and containers, users, groups, computers, and GPOs. Relationships include direct and primary group membership, containment, enabled GPO links, and selected ACL rights such as `GenericAll`, `GenericWrite`, `WriteOwner`, `WriteDacl`, `Owns`, `AllExtendedRights`, `ForceChangePassword`, `AddMember`, and `DCSync`. It reads resource-based constrained delegation directly from the target computer's security descriptor to produce `AllowedToAct` edges, and reads `sIDHistory` to produce `HasSIDHistory` edges. It includes applicable inherited ACEs and conservatively suppresses overlapping deny ACEs for the same trustee. This is an approximation of effective access across group tokens and conditional ACEs.

Native Tessera also records constrained delegation targets and resolves their SPNs against collected objects. It preserves trust direction, type, attributes, and remote domain SID; certificate template flags, EKUs, signatures, and CA publications; and selected enrollment rights. These are inventory facts. Complete derived AD CS escalation rules, effective trust traversal, CA service configuration, and forest-wide orchestration remain development work. Endpoint relationships report observed group membership and logged-on users; firewall, policy, token filtering, and effective credentials can change whether an operation is possible. Rescan to obtain facts that earlier Tessera versions did not collect. Older datasets retain their original coverage.

Live lab validation on 2026-11-14 exercised OU collection on PowerShell 5.1/7, full-domain collection on PowerShell 7, 25-object paging, Deep reports, known fixture paths and negative controls using a default Domain Users LDAP account. It exposed and verified fixes for unreadable account-state attributes and group membership backlinks. See `../Tessera-AD-Lab/LIVE-VALIDATION.md` for results and limits. A comparison against SharpHound `DCOnly`, live paging-session recovery, endpoint access and performance at the intended scale still require validation before relying on coverage decisions. Time collection, merge, indexing, and queries separately.

Run `./tests/Smoke.ps1`, `./tests/Endpoints.ps1`, and `./tests/Scheduler.ps1` for synthetic regression checks. They cover compressed data, preserved evidence, aliases, bounded shard concurrency, and endpoint process reuse, timeouts, and failures. They do not contact a domain controller or live endpoints.

`./tests/LdapRequests.ps1` checks the actual requested attribute lists for RootDSE and paged collection, required/optional naming contexts, ACL controls, and all seven search-constructor sites. LDAP search requests use the typed `.new()` constructor: PowerShell's `New-Object` constructor syntax can turn a multi-attribute array into a single space-joined name, causing missing RootDSE contexts and empty collection attributes. Close and restart the collector after updating `src/BH.Core.psm1`. These checks run without a live LDAP connection on PowerShell 5.1 and 7.

With Node installed, `node ./tests/ReportHelper.cjs` checks explanation coverage, object and identifier search, evidence classification, unknown types, empty data, and safe handling of directory text. Node is used only for this developer check; the report runs in the browser without it.

`node ./tests/Graph.cjs` checks mobile path layout, readable label spacing, touch pan and anchored pinch, tap/drag separation, line selection, explicit helper behavior, node navigation, zoom controls and responsive resizing. The graph remains embedded and offline; no graph library is downloaded.

`./tests/Analysis.ps1` checks complete group-based chains, domain capabilities, stable finding IDs, bounds, comparison states and counts, baseline protection, retained properties, and object inspection. The helper checks also cover finding pagination, step fixes, validation, rollback and review-plan export.

`./tests/Deep.ps1` checks a thirteen-hop path, explicit stop reasons, resume inside a wide edge batch, rejection of changed inputs, more than 200 discovered candidates, cursor pagination, cancellation and time limits, a 270-hop path with detail continuation, and recovery after forcibly terminating an owned test worker mid-transaction. The checks run against synthetic local data on PowerShell 5.1 and 7.

## Source layout

- `Start-Tessera.ps1` provides the GUI and CLI entry points.
- `Tessera.Viewer.ps1` is the standalone full-index viewer; `Build-Viewer.ps1` packages its embedded sources and assets.
- `src/BH.Desktop.Native.cs` implements the default native desktop workspace and graph canvas; its explanations/fixes are packaged from the shared report guidance at build time.
- `src/BH.Viewer.Native.cs`, `src/viewer-launcher.ps1`, `src/viewer-template.html`, and `src/viewer.js` implement its read-only local host, controls, and browser workspace.
- `src/BH.Gui.Layout.ps1` provides the launcher's shared layout and control styles.
- `src/BH.PathExplorer.ps1` provides interactive graph search and pathfinding.
- `src/BH.Core.psm1` handles LDAP collection, scan metadata, and merge.
- `src/BH.Native.cs` handles directory identities, security descriptors, and NDJSON.
- `src/BH.Collection.Native.cs` processes LDAP entries and streams native inventory and relationships.
- `src/BH.Endpoint.psm1`, `src/BH.EndpointWorker.ps1`, and `src/BH.Endpoint.Native.cs` provide bounded native endpoint collection.
- `src/BH.Index.Native.cs` handles disk indexing, deduplication, and graph queries.
- `src/BH.SharpHound.psm1` and `src/BH.SharpHound.Native.cs` import SharpHound ZIP and JSON exports.
- `src/BH.Report.psm1`, `src/BH.Report.Native.cs`, and `src/report-template.html` build the report and optional CSVs.
- `src/report-helper.js` supplies the embedded offline explanations and interactive report tables.
- `src/report-graph.js` supplies the canvas graph, pointer gestures, readable path layout, selection and navigation controls.
- `src/BH.Analysis.Native.cs`, `src/BH.Report.Analysis.ps1`, and `src/report-findings.js` implement exposure discovery, full-index snapshot comparison, object evidence, and review plans.
- `src/BH.Deep.Native.cs` implements disk-backed, checkpointed traversal and finding/path cursors. `src/BH.Analysis.Gui.ps1` and `src/BH.Findings.Gui.ps1` provide analysis settings and full-index candidate paging.
# Pathing comparison update

The native viewer now accepts up to two optional waypoints: **Source → Via 1 → Via 2 → Target**. Blank waypoint fields are skipped. Each leg uses every equally short route through the selected relationships; the details pane preserves leg order even if the graph revisits a node. A failed or limited leg is identified explicitly. **Swap ends** reverses the complete waypoint order. The **Routes** button beside the target shows a target-only overview and ignores source/waypoint fields.

CLI example (object IDs, up to two intermediate IDs):

```powershell
.\Start-Tessera.ps1 -PathQuery -IndexPath 'C:\BH\report\graph.sqlite' -SourceId '<source ID>' -ViaIds '<first ID>','<second ID>' -TargetId '<target ID>'
```

`-MaxVisited` is a per-leg limit (2–250,000; default 250,000), while `-MaxPathEdges` caps the merged graph (1–10,000; default 2,000). In a branching or waypoint result, table rows are unique relationships rather than consecutive chain steps. At small window sizes, scroll the path workspace to reach the full relationship table; the graph keeps a usable minimum height.

Custom path queries in the desktop viewer and collector Path Explorer now show every equally short route. The viewer's **Relationships: all** button selects which relationship types searches can use. Each row in a branching result is a relationship, not a sequential step in a single chain. Target-only **Shortest routes** removes detours before applying the graph edge limit; **All connections** retains the reverse-reachable context.

Read [PATH-PARITY.md](PATH-PARITY.md) for the source comparison, validation, and remaining BloodHound features. Full one-for-one BloodHound product compatibility is not claimed. Existing saved findings keep their original representative chains.

