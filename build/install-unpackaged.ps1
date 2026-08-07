<#
.SYNOPSIS
    Instala o Sintek.Mail no modo sem pacote (unpackaged).

.DESCRIPTION
    Copia a aplicação para a pasta de programas do usuário, cria o atalho no menu Iniciar
    e registra a desinstalação no Painel de Controle.

    O modo sem pacote existe para ambientes em que o sideload de MSIX é bloqueado por
    política. Ele não tem atualização automática: a atualização é reexecutar este script
    com a versão nova, que é justamente o que o App Installer resolve no modo MSIX.

.PARAMETER InstallPath
    Onde instalar. O padrão fica sob %LOCALAPPDATA%, que não exige privilégio de
    administrador — em máquina corporativa esse é o único caminho que costuma funcionar
    sem chamado aberto.

.PARAMETER Uninstall
    Remove a instalação, o atalho e o registro. NÃO apaga o banco local nem as
    credenciais: dados do usuário não somem por desinstalação de programa.

.EXAMPLE
    .\install-unpackaged.ps1

.EXAMPLE
    .\install-unpackaged.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\Sintek.Mail'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppName       = 'Sintek Mail'
$ExecutableName = 'Sintek.Mail.App.exe'
$RegistryKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SintekMail'
$ShortcutPath  = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Remove-Installation {
    if (Test-Path $ShortcutPath) {
        Remove-Item $ShortcutPath -Force
        Write-Host "Atalho removido."
    }

    if (Test-Path $RegistryKey) {
        Remove-Item $RegistryKey -Recurse -Force
        Write-Host "Registro de desinstalação removido."
    }

    if (Test-Path $InstallPath) {
        # O executável pode estar em uso; avisar é melhor que falhar no meio.
        $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExecutableName)) `
            -ErrorAction SilentlyContinue

        if ($running) {
            throw "O $AppName está em execução. Feche-o e execute a desinstalação de novo."
        }

        Remove-Item $InstallPath -Recurse -Force
        Write-Host "Arquivos removidos de $InstallPath."
    }

    Write-Host ""
    Write-Host "Desinstalação concluída."
    Write-Host "O banco local e as credenciais no Gerenciador de Credenciais foram PRESERVADOS."
    Write-Host "Para apagá-los também:"
    Write-Host "  Remove-Item '$env:LOCALAPPDATA\Sintek.Mail' -Recurse"
    Write-Host "  (as credenciais saem pelo Gerenciador de Credenciais do Windows)"
}

function Install-Application {
    $source = $PSScriptRoot

    if (-not (Test-Path (Join-Path $source $ExecutableName))) {
        throw "Execute este script na pasta que contém o $ExecutableName."
    }

    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExecutableName)) `
        -ErrorAction SilentlyContinue

    if ($running) {
        throw "O $AppName está em execução. Feche-o antes de instalar."
    }

    # A pasta é recriada para que uma versão nova não herde arquivos órfãos da anterior —
    # DLL antiga que sobra é a origem clássica do "funciona na máquina limpa".
    if (Test-Path $InstallPath) {
        # appsettings.Local.json guarda a configuração da instalação (Client IDs de OAuth,
        # endereço do assistente local) e precisa sobreviver à atualização.
        $localSettings = Join-Path $InstallPath 'appsettings.Local.json'
        $preserved = if (Test-Path $localSettings) { Get-Content $localSettings -Raw } else { $null }

        Remove-Item $InstallPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null

    Copy-Item -Path (Join-Path $source '*') -Destination $InstallPath -Recurse -Force `
        -Exclude 'install-unpackaged.ps1'

    if ($preserved) {
        $preserved | Out-File -FilePath (Join-Path $InstallPath 'appsettings.Local.json') -Encoding utf8
        Write-Host "Configuração local preservada."
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = Join-Path $InstallPath $ExecutableName
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = 'Cliente de e-mail organizado por Diretório de Domínio'
    $shortcut.Save()

    $version = (Get-Item (Join-Path $InstallPath $ExecutableName)).VersionInfo.ProductVersion
    $size = [math]::Round(((Get-ChildItem $InstallPath -Recurse | Measure-Object Length -Sum).Sum / 1KB))

    New-Item -Path $RegistryKey -Force | Out-Null
    Set-ItemProperty -Path $RegistryKey -Name 'DisplayName'     -Value $AppName
    Set-ItemProperty -Path $RegistryKey -Name 'DisplayVersion'  -Value $version
    Set-ItemProperty -Path $RegistryKey -Name 'Publisher'       -Value 'SINTEK Tecnologia'
    Set-ItemProperty -Path $RegistryKey -Name 'InstallLocation' -Value $InstallPath
    Set-ItemProperty -Path $RegistryKey -Name 'EstimatedSize'   -Value $size -Type DWord
    Set-ItemProperty -Path $RegistryKey -Name 'NoModify'        -Value 1 -Type DWord
    Set-ItemProperty -Path $RegistryKey -Name 'NoRepair'        -Value 1 -Type DWord
    Set-ItemProperty -Path $RegistryKey -Name 'UninstallString' `
        -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallPath\install-unpackaged.ps1`" -Uninstall"

    Copy-Item -Path (Join-Path $source 'install-unpackaged.ps1') -Destination $InstallPath -Force

    Write-Host ""
    Write-Host "$AppName $version instalado em $InstallPath."
    Write-Host "Atalho criado no menu Iniciar."
    Write-Host ""
    Write-Host "Antes do primeiro uso, configure os Client IDs de OAuth em:"
    Write-Host "  $InstallPath\appsettings.Local.json"
    Write-Host "Consulte implantacao.md para o passo a passo."
}

if ($Uninstall) {
    Remove-Installation
} else {
    Install-Application
}
