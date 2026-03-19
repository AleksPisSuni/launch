param(
    [string]$Runtime = 'win-x64'
)

Write-Host "Publishing LaunchpadMapper (net8.0-windows) as single-file for $Runtime"

dotnet restore
dotnet publish -c Release -r $Runtime -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true -o .\dist

if ($LASTEXITCODE -eq 0) { Write-Host 'Publish succeeded. Output: .\dist' } else { throw 'Publish failed' }
