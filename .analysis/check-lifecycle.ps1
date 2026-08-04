Write-Output "=== Ciclo de vida .NET (fonte oficial dotnet/core) ==="
try {
  $r = Invoke-RestMethod -Uri 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json' -TimeoutSec 20
  $r.'releases-index' | Where-Object { $_.'channel-version' -in @('8.0','9.0','10.0') } | ForEach-Object {
    Write-Output ("Canal {0} | tipo: {1} | EOL: {2} | ultimo release: {3}" -f $_.'channel-version', $_.'release-type', $_.'eol-date', $_.'latest-release')
  }
} catch { Write-Output "ERRO: $($_.Exception.Message)" }

Write-Output ""
Write-Output "=== Dependencias do SQLitePCLRaw.bundle_e_sqlcipher 2.1.11 ==="
try {
  $r = Invoke-RestMethod -Uri 'https://api.nuget.org/v3/registration5-gz-semver2/sqlitepclraw.bundle_e_sqlcipher/2.1.11.json' -TimeoutSec 20
  $groups = $r.catalogEntry.dependencyGroups
  foreach ($g in $groups) {
    Write-Output ("TFM: " + $g.targetFramework)
    foreach ($d in $g.dependencies) {
      Write-Output ("  - " + $d.id + " " + $d.range)
    }
  }
} catch { Write-Output "ERRO: $($_.Exception.Message)" }

Write-Output ""
Write-Output "=== Dependencias do Microsoft.Data.Sqlite.Core 10.0.10 ==="
try {
  $r = Invoke-RestMethod -Uri 'https://api.nuget.org/v3/registration5-gz-semver2/microsoft.data.sqlite.core/10.0.10.json' -TimeoutSec 20
  $groups = $r.catalogEntry.dependencyGroups
  foreach ($g in $groups) {
    Write-Output ("TFM: " + $g.targetFramework)
    foreach ($d in $g.dependencies) {
      Write-Output ("  - " + $d.id + " " + $d.range)
    }
  }
} catch { Write-Output "ERRO: $($_.Exception.Message)" }
