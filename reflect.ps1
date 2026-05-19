$dir = "F:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed"
Get-ChildItem "$dir\*.dll" | ForEach-Object { try { [System.Reflection.Assembly]::LoadFrom($_.FullName) | Out-Null } catch {} }
$asm = [System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq "assembly_valheim" } | Select-Object -First 1

function Get-GameType($name) {
    try { return $asm.GetType($name) } catch {}
    # fallback: partial load
    try {
        $loader = [System.Reflection.ReflectionTypeLoadException]
        $types = $null
        try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }
        return $types | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    } catch {}
    return $null
}

Write-Host "=== GameCamera ALL fields ==="
$cam = Get-GameType "GameCamera"
if ($cam) { $cam.GetFields([System.Reflection.BindingFlags]60) | Select-Object Name, IsPublic, IsStatic | Format-Table -AutoSize }

Write-Host "=== GameCamera static properties ==="
if ($cam) { $cam.GetProperties([System.Reflection.BindingFlags]60) | Select-Object Name | Format-Table -AutoSize }
