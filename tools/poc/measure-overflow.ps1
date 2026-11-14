#Requires -Version 7
<#
.SYNOPSIS
    Reports text that does not fit its shape in a rendered .pptx (capacity calibration, docs/plans/pptx-poc-plan.md P5).

.DESCRIPTION
    Opens the file in PowerPoint (COM) without a window and measures each text shape and table:
    - A text shape overflows when the laid-out text (TextFrame2.TextRange.BoundHeight) is taller than the shape
      minus its top and bottom margins.
    - A table overflows when its grown height reaches below the given bottom line (default 492 pt, the footer rule
      of rd-monthly).
    Writes one JSON object per overflow to stdout and exits 1 when there is one, 0 when there is none.

    Requires Windows with PowerPoint installed. Refuses to run while any POWERPNT process exists, so it never
    attaches to the user's open documents. PowerPoint runs one instance, so do not open PowerPoint while the script
    runs: when the instance holds another presentation at the end, the script leaves PowerPoint running, warns and
    exits 2, so that the user's work is never closed.

.PARAMETER Pptx
    The rendered .pptx file.

.PARAMETER BottomLine
    The lowest y (pt) that a table may reach.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Pptx,
    [double] $BottomLine = 492
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$msoTrue = -1
$msoFalse = 0
$tolerance = 0.5

function Get-ComPowerPointId {
    @(Get-CimInstance Win32_Process -Filter "Name = 'POWERPNT.EXE'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '/automation|-Embedding' } | ForEach-Object ProcessId)
}

if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    Write-Error 'PowerPoint is running. Close it first; this script does not attach to an existing PowerPoint.'
    exit 2
}

$file = [IO.Path]::GetFullPath($Pptx).Replace('/', '\')
$app = $null
$presentation = $null
$started = @()
$overflows = [Collections.Generic.List[object]]::new()
$exitCode = 0
try {
    $app = New-Object -ComObject PowerPoint.Application
    $started = @(Get-ComPowerPointId)
    $presentation = $app.Presentations.Open($file, $msoTrue, $msoFalse, $msoFalse)
    foreach ($slide in $presentation.Slides) {
        foreach ($shape in $slide.Shapes) {
            if ($shape.HasTable -eq $msoTrue) {
                $bottom = $shape.Top + $shape.Height
                if ($bottom -gt $BottomLine + $tolerance) {
                    $overflows.Add([ordered]@{ slide = $slide.SlideIndex; shape = $shape.Name; kind = 'table'; bottom = [Math]::Round($bottom, 1); limit = $BottomLine })
                }
            }
            elseif ($shape.HasTextFrame -eq $msoTrue -and $shape.TextFrame2.HasText -eq $msoTrue) {
                $frame = $shape.TextFrame2
                $available = $shape.Height - $frame.MarginTop - $frame.MarginBottom
                $used = $frame.TextRange.BoundHeight
                if ($used -gt $available + $tolerance) {
                    $overflows.Add([ordered]@{ slide = $slide.SlideIndex; shape = $shape.Name; kind = 'text'; used = [Math]::Round($used, 1); available = [Math]::Round($available, 1) })
                }
            }
        }
    }
}
catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    $exitCode = 3
}
finally {
    $ownOpen = 0
    if ($null -ne $presentation) {
        try { $presentation.Close() } catch { $ownOpen = 1 }
    }
    Get-Variable presentation, slide, shape, frame -ErrorAction SilentlyContinue | ForEach-Object { $_.Value = $null }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    $keep = $false
    if ($null -ne $app) {
        try { $keep = $app.Presentations.Count - $ownOpen -gt 0 } catch { $keep = $true }
        if ($keep) {
            Write-Warning 'PowerPoint has another open presentation; it is left running. Do not open PowerPoint while this script runs.'
            $exitCode = 2
        }
        else {
            try { $app.Quit() } catch { }
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
        $app = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
    if (-not $keep) {
        if ($started.Count -eq 0) { $started = @(Get-ComPowerPointId) }
        foreach ($id in $started) {
            $process = Get-Process -Id $id -ErrorAction SilentlyContinue
            if ($process) {
                $process | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
                if (Get-Process -Id $id -ErrorAction SilentlyContinue) { Stop-Process -Id $id -Force }
            }
        }
    }
}

foreach ($overflow in $overflows) { $overflow | ConvertTo-Json -Compress }
if ($exitCode -eq 0 -and $overflows.Count -gt 0) { $exitCode = 1 }
exit $exitCode
