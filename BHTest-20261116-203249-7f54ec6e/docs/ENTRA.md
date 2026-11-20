# Entra ID collector

Run from this folder in Windows PowerShell 5.1 or PowerShell 7:

    .\tools\Collect-Entra.ps1 -TenantId <tenant guid> -ClientId <app guid> -CertificateThumbprint <thumbprint>
    .\tools\Collect-Entra.ps1 -TenantId <tenant guid> -DeviceCode
    .\tools\Collect-Entra.ps1 -TenantId <tenant guid> -ResumeDirectory .\TesseraData\entra-scans\entra-xxxxxxxx-yyyymmdd-hhmmss

Cloud defaults to USGovDoD (dod-graph.microsoft.us, login.microsoftonline.us). Use `-Cloud USGov` for GCC High, `-Cloud Global` for commercial.

## Access
Either a read only app registration (Microsoft Graph application permissions, admin consent):
Directory.Read.All, RoleManagement.Read.Directory, Application.Read.All.
Optional: RoleEligibilitySchedule.Read.Directory (eligible roles, needs Entra ID P2).
Or `-DeviceCode`, where a Global Reader signs in; nothing to configure.

## What it collects
tenant, users, groups, service principals, applications, role definitions, active and eligible role assignments,
group members, owners (groups, apps, service principals), app role assignments (with dangerous Graph permissions flagged),
OAuth consent grants.

## Large tenants
* Lists stream into gzip shards of 10 pages; the paging cursor is saved after each shard.
* Fan-out calls (members, owners, app role assignments) use $batch, 20 per call, with a worker pool that halves on HTTP 429 and grows back on success. Retry-After is honored.
* Ctrl+C or a failure leaves a resumable folder. Finished sections are skipped.
* A section that is forbidden or unlicensed is noted in `entra-manifest.json` and skipped, never fatal.

Output shape matches the AD collector: `nodes_<kind>.*.ndjson.gz` and `edges_<bucket>.*.ndjson.gz` (memberof, acl, access, observed).
Indexing, analysis and viewer support for these Entra kinds is the next milestone.
