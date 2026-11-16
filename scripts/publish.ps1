param([Parameter(Mandatory = $true)][string]$FunctionAppName)

Push-Location (Join-Path $PSScriptRoot '../src/presentation.azure-function')
try {
    func azure functionapp publish $FunctionAppName --dotnet-version 8.0
    if ($LASTEXITCODE -ne 0) { throw "Function publish failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
