# NightLight.ps1
# PowerShell script for controlling Windows 10/11 Night Light feature

# Registry paths for Night Light settings
$stateKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default`$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate"
$settingsKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default`$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings"

# Temperature constants
$MIN_KELVIN = 1200  # Maximum warmth (100% strength)
$MAX_KELVIN = 6500  # Neutral (0% strength)

# Check if Night Light feature is supported
function Test-NightLightSupported {
    return (Test-Path -Path $stateKeyPath) -and (Test-Path -Path $settingsKeyPath)
}

# Get the registry data
function Get-NightLightData {
    if (-not (Test-NightLightSupported)) {
        return $null
    }
    
    try {
        $regItem = Get-ItemProperty -Path $stateKeyPath -Name "Data" -ErrorAction Stop
        return $regItem.Data
    }
    catch {
        Write-Error "Failed to read Night Light registry data: $_"
        return $null
    }
}

# Get the Night Light settings registry data
function Get-NightLightSettingsData {
    if (-not (Test-NightLightSupported)) {
        return $null
    }
    
    try {
        $regItem = Get-ItemProperty -Path $settingsKeyPath -Name "Data" -ErrorAction Stop
        return $regItem.Data
    }
    catch {
        Write-Error "Failed to read Night Light settings data: $_"
        return $null
    }
}

# Check if Night Light is enabled
function Test-NightLightEnabled {
    if (-not (Test-NightLightSupported)) {
        return $false
    }
    
    $data = Get-NightLightData
    if ($null -eq $data) {
        return $false
    }
    
    return $data[18] -eq 0x15  # 21 in decimal
}

# Enable Night Light
function Enable-NightLight {
    if ((Test-NightLightSupported) -and (-not (Test-NightLightEnabled))) {
        Switch-NightLight
    }
}

# Disable Night Light
function Disable-NightLight {
    if ((Test-NightLightSupported) -and (Test-NightLightEnabled)) {
        Switch-NightLight
    }
}

# Switch Night Light state
function Switch-NightLight {
    if (-not (Test-NightLightSupported)) {
        Write-Error "Night Light feature is not supported on this system."
        return
    }
    
    $enabled = Test-NightLightEnabled
    $data = Get-NightLightData
    
    if ($null -eq $data) {
        Write-Error "Could not retrieve Night Light data."
        return
    }
    
    if ($enabled) {
        # Create a 41-element array filled with zeros
        $newData = New-Object byte[] 41
        
        # Copy data[0-21] to newData[0-21]
        [Array]::Copy($data, 0, $newData, 0, [Math]::Min(22, $data.Length))
        
        # Copy data[25-42] to newData[23-40]
        if ($data.Length -gt 25) {
            $copyLength = [Math]::Min($data.Length - 25, 43 - 25)
            [Array]::Copy($data, 25, $newData, 23, $copyLength)
        }
        
        $newData[18] = 0x13
    }
    else {
        # Create a 43-element array filled with zeros
        $newData = New-Object byte[] 43
        
        # Copy data[0-21] to newData[0-21]
        [Array]::Copy($data, 0, $newData, 0, [Math]::Min(22, $data.Length))
        
        # Copy data[23-40] to newData[25-42]
        if ($data.Length -gt 23) {
            $copyLength = [Math]::Min($data.Length - 23, 41 - 23)
            [Array]::Copy($data, 23, $newData, 25, $copyLength)
        }
        
        $newData[18] = 0x15
        $newData[23] = 0x10
        $newData[24] = 0x00
    }
    
    # Increment the first byte in the range 10-14 that isn't 0xff
    for ($i = 10; $i -lt 15; $i++) {
        if ($newData[$i] -ne 0xff) {
            $newData[$i]++
            break
        }
    }
    
    # Update the registry
    try {
        Set-ItemProperty -Path $stateKeyPath -Name "Data" -Value $newData -Type Binary
    }
    catch {
        Write-Error "Failed to update Night Light registry data: $_"
    }
}

# Find a byte sequence in a byte array
function Find-SequenceIndex {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Data,
        [Parameter(Mandatory = $true)][byte[]]$Pattern,
        [int]$StartIndex = 0
    )

    if ($Pattern.Length -eq 0 -or $Data.Length -lt $Pattern.Length) {
        return -1
    }

    for ($i = $StartIndex; $i -le $Data.Length - $Pattern.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Pattern.Length; $j++) {
            if ($Data[$i + $j] -ne $Pattern[$j]) {
                $match = $false
                break
            }
        }
        if ($match) { return $i }
    }

    return -1
}

# Update settings timestamp (5-byte varint) or fallback to version increment
function Update-SettingsTimestamp {
    param([Parameter(Mandatory = $true)][byte[]]$Data)

    $start = -1
    for ($i = 8; $i -le [Math]::Min(30, $Data.Length - 5); $i++) {
        if (($Data[$i] -band 0x80) -ne 0 -and
            ($Data[$i + 1] -band 0x80) -ne 0 -and
            ($Data[$i + 2] -band 0x80) -ne 0 -and
            ($Data[$i + 3] -band 0x80) -ne 0 -and
            ($Data[$i + 4] -band 0x80) -eq 0) {
            $start = $i
            break
        }
    }

    if ($start -ge 0) {
        $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $Data[$start] = [byte](($epoch -band 0x7F) -bor 0x80)
        $Data[$start + 1] = [byte]((($epoch -shr 7) -band 0x7F) -bor 0x80)
        $Data[$start + 2] = [byte]((($epoch -shr 14) -band 0x7F) -bor 0x80)
        $Data[$start + 3] = [byte]((($epoch -shr 21) -band 0x7F) -bor 0x80)
        $Data[$start + 4] = [byte](($epoch -shr 28) -band 0x7F)
        return
    }

    # Fallback: increment first non-0xff byte in 10-14
    for ($i = 10; $i -lt 15; $i++) {
        if ($Data[$i] -ne 0xff) {
            $Data[$i]++
            break
        }
    }
}

# Check if Night Light schedule is enabled
function Test-NightLightScheduleEnabled {
    $data = Get-NightLightSettingsData
    if ($null -eq $data) {
        return $false
    }

    $marker = [byte[]](0xCA, 0x14, 0x0E)
    $idx = Find-SequenceIndex -Data $data -Pattern $marker -StartIndex 0
    if ($idx -lt 2) {
        return $false
    }

    return ($data[$idx - 2] -eq 0x02 -and $data[$idx - 1] -eq 0x01)
}

# Enable/Disable Night Light schedule toggle
function Set-NightLightScheduleEnabled {
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    if (-not (Test-NightLightSupported)) {
        Write-Error "Night Light feature is not supported on this system."
        return
    }

    $data = Get-NightLightSettingsData
    if ($null -eq $data) {
        Write-Error "Could not retrieve Night Light settings data."
        return
    }

    $marker = [byte[]](0xCA, 0x14, 0x0E)
    $idx = Find-SequenceIndex -Data $data -Pattern $marker -StartIndex 0
    if ($idx -lt 2) {
        Write-Error "Schedule marker not found in settings data."
        return
    }

    $hasFlag = ($data[$idx - 2] -eq 0x02 -and $data[$idx - 1] -eq 0x01)
    if ($Enabled -and $hasFlag) {
        return
    }
    if (-not $Enabled -and -not $hasFlag) {
        return
    }

    if ($Enabled) {
        $newData = New-Object byte[] ($data.Length + 2)
        [Array]::Copy($data, 0, $newData, 0, $idx)
        $newData[$idx] = 0x02
        $newData[$idx + 1] = 0x01
        [Array]::Copy($data, $idx, $newData, $idx + 2, $data.Length - $idx)
    }
    else {
        $newData = New-Object byte[] ($data.Length - 2)
        [Array]::Copy($data, 0, $newData, 0, $idx - 2)
        [Array]::Copy($data, $idx, $newData, $idx - 2, $data.Length - $idx)
    }

    Update-SettingsTimestamp -Data $newData

    try {
        Set-ItemProperty -Path $settingsKeyPath -Name "Data" -Value $newData -Type Binary
    }
    catch {
        Write-Error "Failed to update Night Light schedule setting: $_"
    }
}

function Enable-NightLightSchedule {
    Set-NightLightScheduleEnabled -Enabled $true
}

function Disable-NightLightSchedule {
    Set-NightLightScheduleEnabled -Enabled $false
}

# Convert Kelvin temperature to percentage
function ConvertFrom-Kelvin {
    param([int]$kelvin)
    return 100 - (($kelvin - $MIN_KELVIN) / ($MAX_KELVIN - $MIN_KELVIN)) * 100
}

# Convert percentage to Kelvin temperature
function ConvertTo-Kelvin {
    param([int]$percentage)
    return $MAX_KELVIN - ($percentage / 100) * ($MAX_KELVIN - $MIN_KELVIN)
}

# Get current Night Light strength as percentage
function Get-NightLightStrength {
    if (-not (Test-NightLightSupported)) {
        Write-Error "Night Light feature is not supported on this system."
        return 0
    }

    try {
        $data = Get-ItemProperty -Path $settingsKeyPath -Name "Data" -ErrorAction Stop
        if ($null -eq $data) {
            return 0
        }

        # Get temperature bytes from indices 0x23 and 0x24
        $tempLo = $data.Data[0x23]
        $tempHi = $data.Data[0x24]

        # Convert bytes to kelvin
        $kelvin = ($tempHi * 64) + (($tempLo - 128) / 2)

        # Convert kelvin to percentage
        return [Math]::Round((ConvertFrom-Kelvin $kelvin))
    }
    catch {
        Write-Error "Failed to read Night Light strength: $_"
        return 0
    }
}

# Set Night Light strength percentage
function Set-NightLightStrength {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 100)]
        [int]$Percentage
    )

    if (-not (Test-NightLightSupported)) {
        Write-Error "Night Light feature is not supported on this system."
        return
    }

    try {
        # Get current settings data
        $data = Get-ItemProperty -Path $settingsKeyPath -Name "Data" -ErrorAction Stop
        if ($null -eq $data) {
            Write-Error "Could not retrieve Night Light settings data."
            return
        }

        # Convert percentage to kelvin
        $kelvin = ConvertTo-Kelvin $Percentage

        # Calculate bytes for the temperature
        $tempHi = [Math]::Floor($kelvin / 64)
        $tempLo = (($kelvin - ($tempHi * 64)) * 2) + 128

        # Create a copy of the current data
        $newData = $data.Data.Clone()

        # Update temperature bytes (indices 0x23, 0x24)
        $newData[0x23] = $tempLo
        $newData[0x24] = $tempHi

        # Update timestamp bytes
        for ($i = 10; $i -lt 15; $i++) {
            if ($newData[$i] -ne 0xff) {
                $newData[$i]++
                break
            }
        }

        # Update the registry
        Set-ItemProperty -Path $settingsKeyPath -Name "Data" -Value $newData -Type Binary
    }
    catch {
        Write-Error "Failed to update Night Light strength: $_"
    }
}

# Export functions when used as a module
Export-ModuleMember -Function Test-NightLightSupported, Test-NightLightEnabled, Enable-NightLight, Disable-NightLight, Switch-NightLight, Get-NightLightStrength, Set-NightLightStrength, Test-NightLightScheduleEnabled, Set-NightLightScheduleEnabled, Enable-NightLightSchedule, Disable-NightLightSchedule