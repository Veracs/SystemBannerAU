<#
.SYNOPSIS
    Prepares a device for the SystemBanner local classification override.

.DESCRIPTION
    - Creates HKLM:\SOFTWARE\SystemBanner (the override key; NOT under \Policies).
    - Grants the specified group SetValue + read on that key ONLY. No other users gain write access.
    - Registers the "SystemBanner" event source so override audit events (ID 1903) are logged.

    Not needed for local Administrators (the default when the override-groups policy is
    Not Configured): they already have write access and get a UAC prompt.

    This is the ENFORCING control for who can change the banner locally. The policy
    "Choose which groups can change the classification from the right-click menu" only
    controls who sees the menu.
    Use the same group in both places.

    Run elevated. Review with your security lead before deploying (access-control change).

.PARAMETER Group
    Group name (DOMAIN\Group) or SID (S-1-5-...). SID recommended.

.EXAMPLE
    .\Set-SystemBannerOverrideAcl.ps1 -Group 'S-1-5-21-1111111111-2222222222-3333333333-4444'

.EXAMPLE
    .\Set-SystemBannerOverrideAcl.ps1 -Group 'CONTOSO\SystemBanner-Override' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Group
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

# Resolve group to an IdentityReference (validates it exists)
if ($Group -match '^S-1-') {
    $identity = New-Object Security.Principal.SecurityIdentifier($Group)
} else {
    $identity = (New-Object Security.Principal.NTAccount($Group)).Translate([Security.Principal.SecurityIdentifier])
}
Write-Verbose "Resolved group to SID $($identity.Value)"

$keyPath = 'HKLM:\SOFTWARE\SystemBanner'

if (-not (Test-Path $keyPath)) {
    if ($PSCmdlet.ShouldProcess($keyPath, 'Create registry key')) {
        New-Item -Path $keyPath -Force | Out-Null
    }
}

if ($PSCmdlet.ShouldProcess($keyPath, "Grant SetValue/Read to $($identity.Value)")) {
    $acl = Get-Acl -Path $keyPath
    $rights = [System.Security.AccessControl.RegistryRights]'SetValue, ReadKey'
    $rule = New-Object System.Security.AccessControl.RegistryAccessRule(
        $identity, $rights,
        [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -Path $keyPath -AclObject $acl
}

# Register event source for audit logging (requires admin; harmless if already present)
if ($PSCmdlet.ShouldProcess('Application log', 'Register event source SystemBanner')) {
    if (-not [System.Diagnostics.EventLog]::SourceExists('SystemBanner')) {
        New-EventLog -LogName Application -Source 'SystemBanner'
    }
}

Write-Host "Done. Current ACL on ${keyPath}:"
(Get-Acl -Path $keyPath).Access |
    Format-Table IdentityReference, RegistryRights, AccessControlType, IsInherited -AutoSize
