#!/usr/bin/env pwsh

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:AZURE_REGISTRY_NAME -notmatch '^[a-zA-Z0-9]+$') {
    throw 'Invalid Azure registry name.'
}

Write-Output "Building openai-aca-lb:latest..."
az acr build --subscription $env:AZURE_SUBSCRIPTION_ID --registry $env:AZURE_REGISTRY_NAME --image openai-aca-lb:latest ./src/ --output none
if ($LASTEXITCODE -ne 0) { throw 'Container image build failed.' }
$image_name = $env:AZURE_REGISTRY_NAME + '.azurecr.io/openai-aca-lb:latest'
$query = (Get-Content -Raw "$PSScriptRoot/update-image.jmespath").Replace('__IMAGE__', $image_name)
$update_file = New-TemporaryFile
try {
    if ($IsWindows) {
        $access = Get-Acl $update_file.FullName
        $access.SetAccessRuleProtection($true, $false)
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $access.SetAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
        Set-Acl $update_file.FullName $access
    } else {
        [System.IO.File]::SetUnixFileMode($update_file.FullName, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
    }
    $configuration = az containerapp show --subscription $env:AZURE_SUBSCRIPTION_ID --name $env:SERVICE_WEB_NAME --resource-group $env:RESOURCE_GROUP_NAME --query $query --output json
    if ($LASTEXITCODE -ne 0) { throw 'Container App read failed.' }
    $configuration | Set-Content -Path $update_file.FullName -Encoding utf8
    az containerapp update --subscription $env:AZURE_SUBSCRIPTION_ID --name $env:SERVICE_WEB_NAME --resource-group $env:RESOURCE_GROUP_NAME --yaml $update_file.FullName --output none
    if ($LASTEXITCODE -ne 0) { throw 'Container App update failed.' }
} finally {
    Remove-Item -Force $update_file.FullName
}
