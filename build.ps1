# Buduje TwitchProxyBot.exe kompilatorem wbudowanym w Windows (.NET Framework 4.8).
param([string]$out = (Join-Path $PSScriptRoot 'TwitchProxyBot.exe'))
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$fx  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

$wpf = "$fx\WPF"
& "$fx\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" `
    "/win32manifest:$src\app.manifest" "/resource:$src\Main.xaml,Main.xaml" `
    "/lib:$wpf" /r:WindowsBase.dll /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll `
    "$src\App.cs"
if ($LASTEXITCODE -ne 0) { throw "Kompilacja nieudana ($LASTEXITCODE)" }
Write-Host "Zbudowano: $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
