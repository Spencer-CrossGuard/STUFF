# BHTest

BHTest collects Active Directory data over LDAP and produces a local graph index and an offline HTML report. It runs on Windows with Windows PowerShell 5.1 or PowerShell 7. The embedded C# compiles at startup, and the graph uses the SQLite library supplied by Windows. There is no separate database server or PowerShell module to install.

The primary workflow is **Scan → Report → Path Explorer**. BHTest's scanner connects directly to your domain controller using your Windows account or the credentials you supply. SharpHound export import is an optional compatibility feature.

## Portable full-index desktop viewer (no ports)

Run `./BHTest.Viewer.ps1` and choose a completed `graph.sqlite`, or supply its path:

```powershell
./BHTest.Viewer.ps1 -IndexPath './BHData/reports/latest/graph.sqlite'
```

Copy **only `BHTest.Viewer.ps1` and `graph.sqlite`** to another Windows computer to review the data. The default viewer uses native Windows Forms controls and an embedded C# graph canvas. It opens **zero listening ports**, makes no network requests, and needs no browser or WebView runtime. It needs Windows PowerShell 5.1 or PowerShell 7 and Windows' SQLite library. There is no shipped executable, installer, Node runtime, downloaded asset, database service, or additional PowerShell module. Normal script execution and `Add-Type` must be permitted by the host's policy; the launcher does not change execution policy.

The launcher opens a desktop window with **Saved findings**, **Objects & relationships**, **Paths & graph**, and a shared plain-language helper. Drag the graph to pan, use the mouse wheel or zoom buttons, select a relationship for its meaning and recommended fix, and use **Read path / Previous node / Next node** for readable navigation. Keyboard arrows pan, +/− zoom, and 0 fits the graph. Source and target buttons in object search populate the path query. Close the window to release the index. Queries run on a background worker; **Cancel query** stops work, and closing during a query cancels it before releasing the index.

The viewer pages through **every saved candidate and every collected object**, independently of the HTML report's 200-finding preview. It provides full-index name/ID searches, retained object properties, incoming/outgoing relationship pages, plain-language explanations, recommended fixes, and interactive path graphs. Saved paths are loaded in segments of 128 steps with **Continue this path**. On-demand shortest-path searches have no fixed hop cutoff, a selectable 50,000–250,000-identity visit budget, and a 10-second query deadline. A visit/time limit or cancellation is reported as incomplete, distinct from finding no supported path. Continuing an on-demand path searches from the last displayed identity toward the same target; a segment export is explicitly limited and is not a complete-path export.

The selected index is opened read-only and locked against changes for the session. Use a completed standalone snapshot, without a WAL or recovery journal. Generate another report in a different folder, or stop the viewer before replacing this index. Full properties are displayed up to 1 MiB decompressed / 2 MiB stored per record; oversized records receive an explicit notice. The original collection coverage is not retained in the index, so consult the original report and scan metadata alongside the viewer.

Desktop queries use one background worker and an 8 MiB SQLite cache. Candidate paging seeks through the saved ranking index. Name searches and outgoing-edge searches scan bounded disk windows because existing indexes have no name-search or source-edge indexes. The interface advances through empty windows automatically, then offers continuation; an empty partial window is never presented as a completed search. This keeps memory bounded, but a selective search on millions of records can still take time. The viewer retains one result page and one graph segment.

The previous localhost browser interface remains available **only when explicitly requested**:

```powershell
./BHTest.Viewer.ps1 -Browser -IndexPath './BHData/reports/latest/graph.sqlite'
```

Only browser mode binds an available `127.0.0.1` port and creates a private session token; it never binds a LAN interface. Its `-NoBrowser` and `-NoWindow` options require `-Browser`. The default desktop mode never constructs the HTTP host, requests firewall changes, or falls back to opening a port. Browser mode has four bounded worker slots. Click **Stop viewer** in the browser or close its control window to stop it.

Developers regenerate the standalone script with `./Build-Viewer.ps1` after changing its embedded sources. `./tests/Viewer.ps1` exercises the standalone copy with synthetic data, 801 findings over 17 pages, 802 objects, retained compressed properties, 270-hop paths and continuation, bounded queries, rejected unauthorized origins/hosts/sessions, unchanged index hashes, and clean shutdown. Pass `-PowerShellPath` to test another installed PowerShell version.

`./tests/Desktop.ps1 -IndexPath <synthetic-test-graph.sqlite>` tests the desktop controls against the fixture generated by `tests/Viewer.ps1 -Artifacts <directory>`. It checks full candidate paging, global search, path continuation, object evidence, relationship paging, graph rendering/zoom/navigation, shared explanations/fixes, cancellation and safe close, unchanged index hashes, and **zero IPv4/IPv6 TCP listeners belonging to the viewer process**. Use `-ScreenshotDirectory` to render the desktop window for layout review. Desktop and browser modes are checked on PowerShell 5.1 and 7.

## Run a scan

Launch the WinForms interface with `./BH.Gui.ps1`, or use the CLI:

```powershell
./BH.Gui.ps1 -Scan -BaseDn 'DC=corp,DC=local' -Server 'dc01.corp.local' -OutputRoot './BHData/scans'
```

Workers process one LDAP page at a time and write gzip-compressed NDJSON shards directly from C#. The collector divides work by immediate child container and sweeps the base and its remaining direct children. The scheduler keeps at most the configured number of worker pipelines alive. Start with the default of eight workers and a page size of 1,000. Measure elapsed time and domain-controller load before tuning either setting. `-UncompressedOutput` disables compression for scans and merges; both formats can be read together.

Certificate authorities, templates, certificate stores, sites, and domain trust inventory are included by default. `-SkipConfiguration` omits the configuration-partition pass. A scoped OU scan still collects this shared inventory and domain trusts; its account and computer scope remains the selected OU.

Enable **Collect endpoint groups and logons using my Windows account** in the GUI for native endpoint collection, or use:

```powershell
./BH.Gui.ps1 -Scan -BaseDn 'DC=corp,DC=local' -Server 'dc01.corp.local' -CollectEndpoints -EndpointThreads 4 -EndpointTimeoutSeconds 20
```

This phase contacts the collected computers using Windows APIs to enumerate local Administrators, Remote Desktop Users, Distributed COM Users, Remote Management Users, and logged-on users. It produces `AdminTo`, `CanRDP`, `ExecuteDCOM`, `CanPSRemote`, and `HasSession` relationships. It uses the Windows identity running BHTest; credentials supplied for LDAP do not change the endpoint identity. The default is four reusable helper processes and a 20-second deadline per host. An inaccessible or stalled host is recorded individually in `endpoint-status.ndjson.gz` and `endpoint-metadata.json`. A timeout discards that host's unfinished records. The report describes skipped or partial endpoint coverage.

A lost paging session restarts its shard with a fresh cookie and replaces that shard's output. Permanent LDAP failures, worker failures, and object processing errors make the scan incomplete. See `scan-metadata.json` and the worker logs for diagnostics. An incomplete or cancelled scan is excluded from merge and report. The optional CLI switch `-ResolveExternalNames` looks up names for out-of-scope ACL trustees. It adds LDAP and disk work and only changes display labels.

The GUI can run selected OUs sequentially. It skips a selected child when its selected ancestor covers it. If a target fails, the rest still run. Automatic reporting requires all selected targets to finish successfully. Completed targets can be reported on individually. The batch log is written under `BHData`.

## Merge and report

### Import a SharpHound export

On the **Report** tab, click **Import SharpHound**, select one or more ZIP or JSON exports, then click **Generate report**. The importer reads the archive entries directly and writes a dataset that can also be merged with native BHTest scans.

```powershell
./BH.Gui.ps1 -ImportSharpHound -SharpHoundPaths './collection.zip'
```

The importer supports SharpHound JSON schemas 5 and 6. It maps selected ACLs, memberships, sessions, local group rights, and delegation to graph edges. Trust and certificate inventory is preserved where supported, and the report flags the limits of its analysis. BloodHound's derived AD CS and trust paths still require its post-processing. The importer records skipped object types in the dataset and report.

A report accepts raw scan folders and merged datasets, and resolves DN-based edge endpoints and deduplicates relationships across all its inputs. Run a merge when you want a reusable NDJSON dataset:

```powershell
./BH.Gui.ps1 -Merge -ScanPaths './BHData/scans/scan-a', './BHData/scans/scan-b' -MergeOutputRoot './BHData/merged'
./BH.Gui.ps1 -Report -DatasetPaths './BHData/merged/merged-example' -ReportOutputDir './BHData/reports/latest'
```

- `report.html` is a portable dashboard with counts, ACL rankings, a name-search sample, and a bounded four-hop neighborhood around privileged group SIDs. Large datasets still produce a preview. The browser does not load the full graph.
- `graph.sqlite` stores the deduplicated nodes and edges. Open it in **Path Explorer** for full-dataset search and shortest paths.
- Add `-ExportCsv` or check **Export CSV** in the GUI to write the original NDJSON source rows as CSV under `assets`. Use the merged dataset as the input when you want deduplicated CSV rows.

```powershell
./BH.Gui.ps1 -SearchName 'Domain Admins' -IndexPath './BHData/reports/latest/graph.sqlite'
./BH.Gui.ps1 -PathQuery -SourceId 'S-1-5-21-10-20-30-1105' -TargetId 'S-1-5-21-10-20-30-512' -IndexPath './BHData/reports/latest/graph.sqlite'
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

Select **Explain path & fixes** for priority reasons, the complete recorded chain, and candidate points where a permission or access change could break that chain. Select **Explain this step** for a recommended change, the exact affected source and target, prerequisites, validation, and rollback guidance. **Export this review plan** saves those details as text. These recommendations require owner and dependency review; BHTest does not change directory permissions or endpoint configuration. A broken chain does not establish that every alternative route is gone.

Priority is a transparent triage score: a recognized target contributes 30, the strongest capability contributes 15–40, direct group-member breadth can add 5 or 15, and chains of one or two hops add 5. Missing source object records subtract 10. Scores of 65 or more receive high review priority. This ranks the discovered sample, rather than establishing global severity or exploitation probability. Effective access, legitimate service roles, disabled accounts, credential protections and collection coverage still need validation.

Object inspection embeds up to 500 detailed records, prioritizing finding steps and preview objects. Records over 16,384 characters remain in the SQLite index; long property values and lists are shortened for display. Collection metadata describes dataset windows, and merge processing timestamps are identified separately. To see an arbitrary retained object, open **Path Explorer**, search by name or ID, and double-click a result. Its properties and up to 100 related edges appear in a details window. The CLI equivalent is:

```powershell
./BH.Gui.ps1 -InspectObject 'S-1-5-21-10-20-30-1105' -IndexPath './BHData/reports/latest/graph.sqlite'
```

For before-and-after review, check **Compare previous index** in the launcher's Report tab, then select the earlier report's `graph.sqlite` when generating. Choose a new output folder to preserve the baseline. Or run:

```powershell
./BH.Gui.ps1 -Report -DatasetPaths './BHData/scans/after' -ReportOutputDir './BHData/reports/after' -BaselineIndexPath './BHData/reports/before/graph.sqlite'
```

Comparison counts cover the full indexes: added/removed object IDs and added/removed/unchanged relationships, including alias-resolution state. Properties changing on an existing object are not counted as new objects. Current candidate paths are checked against the baseline's recorded relationships, and bounded baseline candidates are checked against the current index. The report distinguishes **Newly observed path**, **Still observed**, and **No longer observed**. Missing evidence can reflect changed scope, permissions, method or collection time, so “no longer observed” does not mean “fixed.” Relationship samples are capped at 50 per direction. Snapshot comparison adds full-index read work and is optional.

### Quick and Deep analysis

On the Report tab, open **Analysis settings**. **Quick** defaults to eight hops and 50,000 identities. **Deep** defaults to no hop cap, no identity cap, and a ten-minute traversal budget. Each budget can be changed; zero removes that particular cap. Time budgets apply to traversal, rather than fingerprinting, indexing, comparison, export or report publication. Discovery never stops just because the HTML preview has 200 candidates.

Deep mode stores its FIFO frontier, predecessor links, ranking and per-node edge cursor in `analysis-checkpoint.sqlite` in the report output folder. Transactions checkpoint small batches. **Cancel** requests a cooperative traversal stop and produces a partial report with its stop reason. Preparation and optional snapshot comparison may finish first. Closing or killing a process can lose its uncommitted batch; SQLite rolls it back when the checkpoint is reopened for resume.

To continue, select **Deep** and **Resume saved analysis**, keep the same datasets and output folder, and raise any exhausted budget. Resuming skips index construction but verifies SHA-256 fingerprints of the dataset files and metadata. Changed inputs or targets are rejected. A fresh deep run refuses to overwrite an existing checkpoint; use a new output folder for a new snapshot. The graph checkpoint must stay paired with its original input files. The published `graph.sqlite` is a separate copy so the report remains readable while a later resume updates the checkpoint; allow disk space for both databases and temporary publication files.

The report displays **Completed**, **DepthLimit**, **VisitLimit**, **TimeLimit**, or **Cancelled**, with the stopping reason, visited/processed/pending counts in the native finding browser and CLI. Completed means the frontier was exhausted for supported relationships and the selected targets. It does not mean every alternative route, all BloodHound relationship types, or every effective-access prerequisite was evaluated.

```powershell
# Deep analysis: no hop or identity cap; ten-minute traversal budget by default.
./BH.Gui.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/deep-a' -AnalysisMode Deep

# Continue the same checkpoint with a larger per-invocation time budget.
./BH.Gui.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/deep-a' -AnalysisMode Deep -ResumeAnalysis -AnalysisSeconds 1800

# Explicit budgets; zero removes the selected cap.
./BH.Gui.ps1 -Report -DatasetPaths './BHData/scans/scan-a' -ReportOutputDir './BHData/reports/custom' -AnalysisMode Deep -AnalysisDepth 0 -AnalysisVisitLimit 2000000 -AnalysisSeconds 0
```

For CLI cancellation, create an `analysis.cancel` file in the output folder while traversal is running. Starting or resuming a report clears an old cancellation marker. Concurrent report writes to the same output folder are rejected with an exclusive process-held lock. Use separate output folders for concurrent runs. Resume keeps existing discoveries; lowering a budget does not remove candidates already found.

**Path Explorer → Findings** loads 100 rows per page from the complete discovery index. Its Next/Previous controls use score-and-sequence cursors, rather than loading the full list or repeatedly skipping earlier rows. The CLI equivalents are:

```powershell
./BH.Gui.ps1 -FindingPage -IndexPath './BHData/reports/deep-a/graph.sqlite' -FindingPageSize 100
# For the next page, use the last row's Score and Sequence.
./BH.Gui.ps1 -FindingPage -IndexPath './BHData/reports/deep-a/graph.sqlite' -AfterScore 70 -AfterSequence 501 -FindingPageSize 100
```

A finding's displayed path is limited to its first 256 steps independently of traversal depth. Such a finding has `PathLimited`, the actual `HopCount`, and `NextPathId`; its truncated preview is not used to assert that the complete path persisted or disappeared in a baseline. Continue its recorded chain with `-TraceFinding <NextPathId> -IndexPath <graph.sqlite>`, which returns up to 256 more steps per call. Direct shortest-path queries in Path Explorer retain their separate identity visit budget. The SQLite library supplied by Windows must support row-value comparisons (SQLite 3.15 or later).

## Large datasets

Collection and file import stream records; the index resolves aliases, deduplicates edges, and executes graph queries on disk. Reports retain the collected object properties in SQLite, with larger records compressed internally. Merged datasets are compressed by default. CSV export is optional because it adds another full read and potentially large output. Allow disk space for source data, the index, and a temporary index when rebuilding a report or merging.

Before the disk-backed deep-search engine was added, a local synthetic run with the bounded findings pipeline processed **3,000,004 objects and 12,000,013 relationships**. Native record writing took **22.22 seconds**, indexing, bounded exposure analysis and report generation **54.03 seconds**, and peak process memory was **316 MB**. Compressed scan files occupied **121.1 MB**, and the retained-property index **2,203.9 MB**. A known one-hop query took **1.57 ms**. This exercises the actual native writer and report pipeline with generated directory entries; it excludes LDAP/network latency, endpoint collection and optional snapshot comparison. Real schemas, property sizes, graph shape, hardware, and remote response times affect these figures.

## Coverage and validation

A synthetic deep-search run processed **1,000,004 objects and 4,000,014 relationships**, visited **1,000,005 identities** (including referenced outside identities), and discovered **1,000,002 candidates**. Complete indexing, traversal and reporting took **170.63 seconds**, including **149.2 seconds** of traversal. Peak process memory was **341.9 MB**; the index including search state occupied **927.6 MB**. Retrieving 100 finding records near the 900,000th result took **10.21 ms**. The HTML embedded 200 candidates while the full list remained in SQLite. This intentionally broad synthetic graph differs from the earlier bounded benchmark and excludes live LDAP/endpoint latency and snapshot comparison.

The collector records domains, OUs and containers, users, groups, computers, and GPOs. Relationships include direct and primary group membership, containment, enabled GPO links, and selected ACL rights such as `GenericAll`, `GenericWrite`, `WriteOwner`, `WriteDacl`, `Owns`, `AllExtendedRights`, `ForceChangePassword`, `AddMember`, and `DCSync`. It reads resource-based constrained delegation directly from the target computer's security descriptor to produce `AllowedToAct` edges, and reads `sIDHistory` to produce `HasSIDHistory` edges. It includes applicable inherited ACEs and conservatively suppresses overlapping deny ACEs for the same trustee. This is an approximation of effective access across group tokens and conditional ACEs.

Native BHTest also records constrained delegation targets and resolves their SPNs against collected objects. It preserves trust direction, type, attributes, and remote domain SID; certificate template flags, EKUs, signatures, and CA publications; and selected enrollment rights. These are inventory facts. Complete derived AD CS escalation rules, effective trust traversal, CA service configuration, and forest-wide orchestration remain development work. Endpoint relationships report observed group membership and logged-on users; firewall, policy, token filtering, and effective credentials can change whether an operation is possible. Rescan to obtain facts that earlier BHTest versions did not collect. Older datasets retain their original coverage.

The collector still needs a live test domain and comparison against SharpHound `DCOnly` before relying on it for coverage decisions. Start with a test OU and verify counts and known paths, then test paging recovery, access-denied behavior, and performance at the intended scale. Time collection, merge, indexing, and queries separately.

Run `./tests/Smoke.ps1`, `./tests/Endpoints.ps1`, and `./tests/Scheduler.ps1` for synthetic regression checks. They cover compressed data, preserved evidence, aliases, bounded shard concurrency, and endpoint process reuse, timeouts, and failures. They do not contact a domain controller or live endpoints.

`./tests/LdapRequests.ps1` checks the actual requested attribute lists for RootDSE and paged collection, required/optional naming contexts, ACL controls, and all seven search-constructor sites. LDAP search requests use the typed `.new()` constructor: PowerShell's `New-Object` constructor syntax can turn a multi-attribute array into a single space-joined name, causing missing RootDSE contexts and empty collection attributes. Close and restart the collector after updating `src/BH.Core.psm1`. These checks run without a live LDAP connection on PowerShell 5.1 and 7.

With Node installed, `node ./tests/ReportHelper.cjs` checks explanation coverage, object and identifier search, evidence classification, unknown types, empty data, and safe handling of directory text. Node is used only for this developer check; the report runs in the browser without it.

`node ./tests/Graph.cjs` checks mobile path layout, readable label spacing, touch pan and anchored pinch, tap/drag separation, line selection, explicit helper behavior, node navigation, zoom controls and responsive resizing. The graph remains embedded and offline; no graph library is downloaded.

`./tests/Analysis.ps1` checks complete group-based chains, domain capabilities, stable finding IDs, bounds, comparison states and counts, baseline protection, retained properties, and object inspection. The helper checks also cover finding pagination, step fixes, validation, rollback and review-plan export.

`./tests/Deep.ps1` checks a thirteen-hop path, explicit stop reasons, resume inside a wide edge batch, rejection of changed inputs, more than 200 discovered candidates, cursor pagination, cancellation and time limits, a 270-hop path with detail continuation, and recovery after forcibly terminating an owned test worker mid-transaction. The checks run against synthetic local data on PowerShell 5.1 and 7.

## Source layout

- `BH.Gui.ps1` provides the GUI and CLI entry points.
- `BHTest.Viewer.ps1` is the standalone full-index viewer; `Build-Viewer.ps1` packages its embedded sources and assets.
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
