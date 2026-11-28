# Definitions shared by Build-App.ps1 and Make-Installer.ps1 (dot-sourced), so an app is identified the same way
# whichever of them built or installed it.

# The app's version: <Version> in src\WinMuse\WinMuse.csproj (also stamped on the exe and shown in Settings > About).
function Get-AppVersion([string]$root) {
    $project = Join-Path $root 'src\WinMuse\WinMuse.csproj'
    return ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

# The key an installed app is listed under in Settings > Apps, from its name: "Music Player" -> "WinMuse-MusicPlayer".
function Get-AppRegKey([string]$name) {
    return 'WinMuse-' + ((($name -replace '[^\w\- ]', '').Trim()) -replace ' ', '')
}

# The Windows Installer upgrade code for an app name: a name-based GUID (RFC 4122 version 3), so every .msi made for
# the same name replaces the previous one, and Build-App.ps1 can find what it installed.
function Get-MsiUpgradeCode([string]$name) {
    $hash = [Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('WinMuse.Msi.' + (Get-AppRegKey $name)))
    $hash[6] = ($hash[6] -band 0x0F) -bor 0x30
    $hash[8] = ($hash[8] -band 0x3F) -bor 0x80
    return (New-Object Guid (, $hash)).ToString('B').ToUpper()
}
