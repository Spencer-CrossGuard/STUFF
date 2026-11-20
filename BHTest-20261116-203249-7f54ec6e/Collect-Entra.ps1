# Collects an Entra ID tenant (same parameters as Start-BHEntraCollection). Examples:
#   .\Collect-Entra.ps1 -TenantId <guid> -ClientId <guid> -CertificateThumbprint <thumbprint>
#   .\Collect-Entra.ps1 -TenantId <guid> -DeviceCode            (sign in as a Global Reader)
#   .\Collect-Entra.ps1 -TenantId <guid> -ResumeDirectory .\entra-scans\entra-xxxx   (continue a stopped run)
# Defaults to USGovDoD. Add -Cloud USGov for GCC High or -Cloud Global for commercial.
[CmdletBinding()]
param(
    [ValidateSet('USGovDoD', 'USGov', 'Global', 'China')][string]$Cloud = 'USGovDoD',
    [Parameter(Mandatory)][string]$TenantId,
    [string]$ClientId,
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [securestring]$PfxPassword,
    [securestring]$ClientSecret,
    [switch]$DeviceCode,
    [securestring]$AccessToken,
    [string]$OutputRoot = (Join-Path (Get-Location) 'entra-scans'),
    [string]$ResumeDirectory,
    [ValidateRange(1, 64)][int]$Threads = 16,
    [ValidateSet('All', 'RoleAssignable', 'None')][string]$Members = 'All',
    [switch]$SkipOwners,
    [switch]$SkipEligibleRoles,
    [switch]$SkipAppRoleAssignments,
    [string[]]$Sections,
    [switch]$NoCompress,
    [string]$GraphUrl,
    [string]$LoginUrl
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'src\BH.Entra.psm1') -Force -DisableNameChecking
Start-BHEntraCollection @PSBoundParameters
