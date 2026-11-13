# BHTest setup for the Microsoft deployment lab kit

The Windows 11 / Microsoft 365 Deployment Lab Kit already provides a domain controller, domain-joined clients and management infrastructure. Import and provision the kit using Microsoft's included setup guide first. These scripts populate that existing lab; they do not create a forest, import VMs, install roles, change the host, or depend on a hard-coded kit domain/computer name.

## What the setup creates

Default: 120 background users, 14 named scenario/service/control accounts, 12 groups, and an owned `OU=BHTestLab` containing People, Groups and Services OUs. Use `-UserCount` on the first run to choose 10–5,000 background users. This is a functional lab fixture, not a million-object AD load generator.

Real AD group memberships and DACL entries create these custom-target paths:

- Maya → Helpdesk group → reset Deploy Service → write Application Service → edit AppAdmins ACL → manage membership of the lab PathTarget group.
- Ethan → full control of Backup Service → ownership-write capability on the lab PathTarget group.
- Priya → full control of the lab Identity Sync Service. With the explicit replication option, that service has real replication permissions on the lab domain.
- A distribution-group membership control, an explicit allow/deny membership-control pair, a disabled leaver, and disconnected users provide negative and prerequisite checks.

`BH-Lab-PathTarget` is an ordinary lab security group, **not Domain Admins**. The basic fixture supports exact-target path queries without granting domain-wide authority. Domain/Domain Admins paths require the optional elevated scenarios below.

Three attribute-specific ACLs are coverage probes: key-credential write, SPN write, and (when a member target is supplied) delegation-descriptor write. **The current native BHTest ACL parser does not derive the AddKeyCredentialLink, WriteSPN or AddAllowedToAct labels.** These are known collector-gap checks; creating a real ACL does not mean BHTest collected it. Actual resource-based delegation, LAPS/gMSA secrets, PKI enrollment, GPO takeover and trust attacks are not configured by this initial script. No credentials are extracted or exported.

## Before applying

Use an isolated, disposable copy of the evaluation kit. Take VM checkpoints of the kit domain controller and any selected member VM before applying the fixture. The optional replication and privileged-user scenarios intentionally grant powerful rights. Use only lab passwords. Reset the fixture by restoring the matching pre-run VM checkpoints; these scripts do not implement automatic deletion or rollback.

## 1. Preview anywhere, with no AD connection

```powershell
.\Setup-BHTestADLab.ps1 -Mode Plan

# Optional profile preview. This is still offline and changes no AD objects.
.\Setup-BHTestADLab.ps1 -Mode Apply -ExpectedDomain example.test `
  -IncludeReplicationScenario -IncludeTier0Account -WhatIf
```

`Plan` and `-WhatIf` do not discover or validate a live domain. `-OutputDirectory` optionally saves their JSON preview. They never prompt for passwords.

## 2. Run inside the kit domain controller

Open **64-bit Windows PowerShell 5.1 as the kit domain administrator** inside its DC VM. Discover the actual names rather than assuming the kit version:

```powershell
Import-Module ActiveDirectory
Get-ADDomain | Select-Object DNSRoot, DistinguishedName, NetBIOSName
Get-ADComputer -Filter * | Select-Object Name, DNSHostName
```

Replace `YOUR.LAB.DOMAIN` and `YOUR-MEMBER-VM` below with the kit values you just checked. The selected member must be a real domain-joined non-DC VM; omit `-DelegationTargetComputer` to skip changes to that member computer's AD object.

Basic fixture, with permissions confined to its owned OU:

```powershell
.\Setup-BHTestADLab.ps1 -Mode Apply -LabOnly `
  -ExpectedDomain 'YOUR.LAB.DOMAIN' -OutputDirectory C:\BHTestLab
```

Recommended fuller validation profile for an isolated kit:

```powershell
.\Setup-BHTestADLab.ps1 -Mode Apply -LabOnly `
  -ExpectedDomain 'YOUR.LAB.DOMAIN' -OutputDirectory C:\BHTestLab `
  -IncludeReplicationScenario -IncludeTier0Account `
  -DelegationTargetComputer 'YOUR-MEMBER-VM'
```

It prompts securely for a password satisfying the domain policy for **new** ordinary lab accounts, plus a separate password for a **new** `bht.tier0` account when selected. No shared/default password is embedded. Existing account passwords are never reset on rerun. Never reuse production credentials.

Optional changes outside the owned OU:

- `-IncludeReplicationScenario`: adds three explicit replication-right ACEs for the owned `bht.svcsync` account on the lab domain root. These are real powerful permissions.
- `-IncludeTier0Account`: adds only the owned `bht.tier0` account to the domain's actual Domain Admins group. It does not edit AdminSDHolder or protected-account ACLs.
- `-DelegationTargetComputer`: grants the owned `bht.lucas` user an attribute-specific delegation-descriptor write ACE on that member's AD object, and configures an owned legacy service account's constrained-delegation target. It does not replace existing delegation descriptors, move the computer, or deploy a working service. Targeting a domain controller is refused.

All AD cmdlets and ADSI ACL operations are pinned to the local kit DC. Apply requires a virtual domain controller, an exact domain-name match, `-LabOnly`, and ownership markers. Unowned objects with colliding names are refused. Existing ACL entries and memberships are preserved. Subsequent runs use the **same flags, UserCount and output directory** as the first run; elevated scenarios are never silently removed or omitted from the manifest. Existing lab OUs require their original manifest directory.

The script reads back memberships, explicit ACL entries, account state and selected optional configuration. It writes:

- `lab-manifest.json`: lab/domain identity, owned principal SIDs/DNs, selected profile, expected relationships, and a mutation journal.
- `verification.json`: actual directory checks and failures. This checks directory configuration, not successful exploitation or collector completeness.

If Apply fails, it stops and records partial state where possible. It does not silently continue or roll back other kit objects. A rerun preserves existing passwords. Restore the checkpoints for a clean reset.

For an AD read-only recheck of the fuller profile, reuse its exact options and original output directory:

```powershell
.\Setup-BHTestADLab.ps1 -Mode Verify `
  -ExpectedDomain 'YOUR.LAB.DOMAIN' -OutputDirectory C:\BHTestLab `
  -IncludeReplicationScenario -IncludeTier0Account `
  -DelegationTargetComputer 'YOUR-MEMBER-VM'
```

Verify writes local verification/manifest files but makes no AD changes. Omit the optional flags only if they were omitted on the original Apply.

## 3. Configure a real member VM, if endpoint tests are wanted

Copy `Configure-BHTestLabEndpoint.ps1` and the verified `lab-manifest.json` to the selected domain-joined client/member VM. Run an elevated **64-bit** PowerShell there:

```powershell
.\Configure-BHTestLabEndpoint.ps1 -ManifestPath .\lab-manifest.json `
  -Mode Plan -ExpectedComputer 'YOUR-MEMBER-VM' `
  -EnableAdminPath -EnableRemoteMemberships

.\Configure-BHTestLabEndpoint.ps1 -ManifestPath .\lab-manifest.json `
  -Mode Apply -LabOnly -ExpectedDomain 'YOUR.LAB.DOMAIN' `
  -ExpectedComputer 'YOUR-MEMBER-VM' `
  -EnableAdminPath -EnableRemoteMemberships -OutputDirectory C:\BHTestLab
```

This checks the VM/domain name, actual domain SID, OU ownership marker and selected group SIDs against AD before changing local membership. It refuses a DC. `-EnableAdminPath` adds only the lab AppAdmins group to local Administrators. `-EnableRemoteMemberships` adds only the lab RemoteUsers group to Remote Desktop Users and Remote Management Users, using their well-known SIDs to support localized Windows names. Existing local memberships are retained.

It **does not** open firewall rules, turn on RDP/WinRM, install software or fabricate sessions. The native endpoint collector still needs suitable access and existing Windows firewall policy. Failures must be treated as incomplete coverage.

For a real `HasSession` example, sign in interactively to this member VM with the lab `bht.tier0` account created by the explicit option. Keep the session active during endpoint collection. This gives a recorded path through local administration and a privileged session; it does not establish that credentials can be recovered. Sign out afterward. Do not sign this account into your host or production devices.

## 4. Collect and compare

1. For a small first LDAP check, scan the owned OU printed by setup.
2. To include the domain root, actual Domain Admins and the existing kit computers, collect the **whole disposable lab domain naming context**, not only `OU=BHTestLab`. Keep configuration-partition collection if testing its inventory.
3. Enable native endpoint collection only for the disposable lab scope when testing local groups and sessions.
4. Generate a Deep report. Inspect collection failures and analysis stop reasons.
5. Query Maya → `BH-Lab-PathTarget` for the basic fixture. With replication selected, expect Priya → Sync Service → lab domain (`GenericAll`, `DCSync`). With endpoint group setup and a real Tier 0 session, query Maya → actual Domain Admins.
6. Compare the manifest/verification records with collected objects and edges. The explicit-deny and distribution-group controls must not become authorization shortcuts. Disabled-account state and observed sessions need separate interpretation.

Directory ACLs approximate effective access; token membership, deny entries, account protections, authentication, service configuration, network access and observation freshness still matter. An absent path is not proof that the domain has no exposure.

## Verification without a running AD lab

`tests\Test-LabScripts.ps1` parses the scripts on PowerShell 5.1/7, exercises offline previews and refusal guards, and tests real .NET AD security descriptors against BHTest's native ACL parser. It checks ACL idempotence/preservation using in-memory directory adapters and verifies cached membership additions retain unrelated members. These are local tests, **not a substitute for executing Apply/Verify inside the downloaded kit**. No live AD execution has been performed while preparing this package.

## References

- [Microsoft deployment lab kit overview](https://learn.microsoft.com/en-us/microsoft-365/enterprise/modern-desktop-deployment-and-management-lab?view=o365-worldwide)
- [New-ADUser reference](https://learn.microsoft.com/en-us/powershell/module/activedirectory/new-aduser?view=windowsserver2025-ps)
- [AD access-rule constructors](https://learn.microsoft.com/en-us/dotnet/api/system.directoryservices.activedirectoryaccessrule.-ctor?view=windowsdesktop-10.0)
- [Password-reset extended right](https://learn.microsoft.com/en-us/windows/win32/adschema/r-user-force-change-password)
