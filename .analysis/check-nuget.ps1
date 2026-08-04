$pkgs = @(
  'Microsoft.WindowsAppSDK',
  'Microsoft.EntityFrameworkCore.Sqlite',
  'Microsoft.Data.Sqlite.Core',
  'SQLitePCLRaw.bundle_e_sqlcipher',
  'SQLitePCLRaw.core',
  'MailKit',
  'MimeKit',
  'CommunityToolkit.Mvvm',
  'HtmlSanitizer',
  'Microsoft.Identity.Client',
  'Microsoft.Identity.Client.Broker',
  'Google.Apis.Auth',
  'Microsoft.Web.WebView2',
  'Microsoft.Windows.CsWin32',
  'Polly',
  'xunit',
  'AwesomeAssertions',
  'FluentAssertions'
)
foreach ($p in $pkgs) {
  try {
    $r = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$($p.ToLower())/index.json" -TimeoutSec 20
    $stable = $r.versions | Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
    $latest = $r.versions[-1]
    Write-Output ("{0} => estavel: {1} | ultima: {2}" -f $p, $stable, $latest)
  } catch {
    Write-Output ("{0} => ERRO: {1}" -f $p, $_.Exception.Message)
  }
}
