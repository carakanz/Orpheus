# Служебный: кириллическое сообщение коммита — UTF-8 без BOM для `git commit -F`.
# Использование: powershell -ExecutionPolicy Bypass -File tools\mkmsg.ps1 -Src msg.txt -Dst msg_utf8.txt
param(
    [Parameter(Mandatory = $true)][string]$Src,
    [Parameter(Mandatory = $true)][string]$Dst
)
$text = [IO.File]::ReadAllText((Resolve-Path $Src))
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Dst), $text, $utf8NoBom)
Write-Host "msg -> $Dst ($((Get-Item $Dst).Length) bytes)"
