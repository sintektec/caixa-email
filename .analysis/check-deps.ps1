function Get-NuspecDeps($id, $version) {
  $url = "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/$version/$($id.ToLower()).nuspec"
  try {
    [xml]$n = Invoke-RestMethod -Uri $url -TimeoutSec 20
    Write-Output "### $id $version"
    $groups = $n.package.metadata.dependencies.group
    if ($null -eq $groups) {
      $dep = $n.package.metadata.dependencies.dependency
      if ($dep) { foreach ($d in $dep) { Write-Output ("  (sem TFM) " + $d.id + " " + $d.version) } }
      else { Write-Output "  (sem dependencias)" }
    } else {
      foreach ($g in $groups) {
        Write-Output ("  TFM: " + $g.targetFramework)
        foreach ($d in $g.dependency) { Write-Output ("    - " + $d.id + " " + $d.version) }
      }
    }
  } catch { Write-Output "### $id $version => ERRO: $($_.Exception.Message)" }
  Write-Output ""
}

Get-NuspecDeps 'SQLitePCLRaw.bundle_e_sqlcipher' '2.1.11'
Get-NuspecDeps 'Microsoft.Data.Sqlite.Core' '10.0.10'
Get-NuspecDeps 'Microsoft.EntityFrameworkCore.Sqlite' '10.0.10'
Get-NuspecDeps 'Microsoft.WindowsAppSDK' '2.3.1'
