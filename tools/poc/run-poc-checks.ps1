#Requires -Version 7
<#
.SYNOPSIS
    PowerPoint checks of rendered .pptx files (docs/plans/pptx-poc-plan.md P4 / P8; P5 / P6 PNG export).

.DESCRIPTION
    For each file: opens it in PowerPoint (COM) without errors, exports every slide of the unmodified file as PNG,
    then edits one placeholder text, one table cell and one slide's layout, saves a copy as edited.pptx, reopens the
    copy and checks the edits. Output goes to out/poc/<stem>/ (stem = file name without extension).

    Requires Windows with PowerPoint installed. Refuses to run while any POWERPNT process exists, so it never
    touches the user's open documents. All PowerPoint work runs in one background job with a timeout (a repair
    prompt or modal dialog shows up as a timeout). Whatever the outcome, the POWERPNT process started for a file is
    ended before the next file. Exit code: 0 when every file passes, 1 otherwise, 2 when PowerPoint was running.

    Call it with -Command so that -Pptx receives an array:
        pwsh -Command "& tools/poc/run-poc-checks.ps1 -Pptx out/poc/monthly.pptx,out/poc/stress.pptx"

.PARAMETER Pptx
    The .pptx files to check.

.PARAMETER OutputRoot
    Output directory (default out/poc, relative to the current directory).

.PARAMETER TimeoutSeconds
    Time limit of the PowerPoint job per file (default 300).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Pptx,
    [string] $OutputRoot = 'out/poc',
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullWindowsPath([string] $Path) {
    # COM rejects forward slashes (Slide.Export, SaveAs); always pass a full backslash path.
    [IO.Path]::GetFullPath($Path).Replace('/', '\')
}

# Runs inside the job (a separate pwsh process). Returns one result object; never throws.
$comChecks = {
    param([string] $File, [string] $OutDir, [string] $PidFile)

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $msoTrue = -1
    $msoFalse = 0
    $msoPlaceholder = 14
    $ppSaveAsOpenXMLPresentation = 24
    $SwitchToLayout = 'Two Content'
    $EditedTitle = 'PoC edited title'
    $EditedCell = 'PoC edited cell'

    $result = [ordered]@{
        file = $File; ok = $false; opened = $false; slide_count = 0; pngs = @()
        edited_text_slide = $null; edited_table_slide = $null; switched_layout_slide = $null
        saved = $null; reopened = $false; verified = [ordered]@{}; error = $null
    }
    $app = $null
    try {
        $app = New-Object -ComObject PowerPoint.Application
        Get-Process POWERPNT -ErrorAction SilentlyContinue | ForEach-Object Id | Set-Content -LiteralPath $PidFile

        # 1. Open the unmodified file and export every slide.
        $presentation = $app.Presentations.Open($File, $msoTrue, $msoFalse, $msoFalse)
        $result.opened = $true
        $result.slide_count = $presentation.Slides.Count
        foreach ($slide in $presentation.Slides) {
            $png = Join-Path $OutDir ('slide-{0:D2}.png' -f $slide.SlideIndex)
            $slide.Export($png, 'PNG', 1600, 900)
            $result.pngs += [IO.Path]::GetFileName($png)
        }

        # 2. Edit a placeholder text (first slide's first text placeholder).
        $first = $presentation.Slides.Item(1)
        foreach ($shape in $first.Shapes) {
            if ($shape.Type -eq $msoPlaceholder -and $shape.HasTextFrame -eq $msoTrue) {
                $shape.TextFrame.TextRange.Text = $EditedTitle
                $result.edited_text_slide = 1
                break
            }
        }

        # 3. Edit a table cell (first table, first data row, first column).
        :tables foreach ($slide in $presentation.Slides) {
            foreach ($shape in $slide.Shapes) {
                if ($shape.HasTable -eq $msoTrue) {
                    $shape.Table.Cell(2, 1).Shape.TextFrame.TextRange.Text = $EditedCell
                    $result.edited_table_slide = $slide.SlideIndex
                    break tables
                }
            }
        }

        # 4. Switch the last slide to another layout.
        $layout = $null
        foreach ($candidate in $presentation.SlideMaster.CustomLayouts) {
            if ($candidate.Name -eq $SwitchToLayout) { $layout = $candidate }
        }
        if ($null -eq $layout) { throw "Layout '$SwitchToLayout' not found." }
        $last = $presentation.Slides.Item($presentation.Slides.Count)
        $last.CustomLayout = $layout
        $result.switched_layout_slide = $last.SlideIndex

        # 5. Save as a copy and close.
        $saved = Join-Path $OutDir 'edited.pptx'
        $presentation.SaveAs($saved, $ppSaveAsOpenXMLPresentation)
        $presentation.Close()
        $presentation = $null
        $result.saved = [IO.Path]::GetFileName($saved)

        # 6. Reopen the copy and check the edits.
        $presentation = $app.Presentations.Open($saved, $msoTrue, $msoFalse, $msoFalse)
        $result.reopened = $true
        $titles = @(foreach ($shape in $presentation.Slides.Item(1).Shapes) {
            if ($shape.HasTextFrame -eq $msoTrue) { $shape.TextFrame.TextRange.Text }
        })
        $result.verified.text = $titles -contains $EditedTitle
        $result.verified.table = $false
        if ($null -ne $result.edited_table_slide) {
            foreach ($shape in $presentation.Slides.Item($result.edited_table_slide).Shapes) {
                if ($shape.HasTable -eq $msoTrue) {
                    $result.verified.table = $shape.Table.Cell(2, 1).Shape.TextFrame.TextRange.Text -eq $EditedCell
                }
            }
        }
        $result.verified.layout = $presentation.Slides.Item($presentation.Slides.Count).CustomLayout.Name -eq $SwitchToLayout
        $result.verified.slide_count = $presentation.Slides.Count -eq $result.slide_count
        $presentation.Close()
        $presentation = $null

        $result.ok = ($null -ne $result.edited_text_slide) -and ($null -ne $result.edited_table_slide) -and
            -not ($result.verified.Values -contains $false)
    }
    catch {
        $result.error = $_.Exception.Message
    }
    finally {
        # Release COM references before Quit, otherwise POWERPNT can stay alive after Quit.
        Get-Variable presentation, slide, shape, first, last, layout, candidate -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Value = $null }
        [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
        if ($null -ne $app) {
            try { $app.Quit() } catch { }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
            $app = $null
            [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        }
    }
    [pscustomobject]$result
}

# A process that was just killed can still be listed (HasExited = true) while handles to it are open.
function Get-RunningPowerPointId {
    @(Get-Process POWERPNT -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited } | ForEach-Object Id)
}

function Test-Running([int] $Id) {
    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    $null -ne $process -and -not $process.HasExited
}

function Stop-StartedPowerPoint([int[]] $Before, [string] $PidFile) {
    $ids = @()
    if (Test-Path -LiteralPath $PidFile) { $ids += @(Get-Content -LiteralPath $PidFile | ForEach-Object { [int]$_ }) }
    # Also any POWERPNT that appeared since the start (the job may time out before it writes the PID file).
    $ids += @(@(Get-RunningPowerPointId) | Where-Object { $Before -notcontains $_ })
    foreach ($id in ($ids | Sort-Object -Unique)) {
        if (Test-Running $id) {
            Wait-Process -Id $id -Timeout 30 -ErrorAction SilentlyContinue
            if (Test-Running $id) {
                Write-Warning "POWERPNT $id still running after 30 s; killing it."
                Stop-Process -Id $id -Force
                Wait-Process -Id $id -Timeout 15 -ErrorAction SilentlyContinue
            }
        }
    }
    $left = @($ids | Where-Object { Test-Running $_ })
    if ($left.Count -gt 0) { throw "POWERPNT process(es) $($left -join ', ') could not be ended." }
}

$exitCode = 0
foreach ($path in $Pptx) {
    $file = Get-FullWindowsPath $path
    $stem = [IO.Path]::GetFileNameWithoutExtension($file)
    $outDir = Get-FullWindowsPath (Join-Path $OutputRoot $stem)

    $before = @(Get-RunningPowerPointId)
    if ($before.Count -gt 0) {
        Write-Error 'PowerPoint is running. Close it first; this script does not attach to an existing PowerPoint.' -ErrorAction Continue
        exit 2
    }

    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir | Out-Null
    $pidFile = Join-Path $outDir 'powerpnt.pid'

    $result = $null
    $job = Start-Job -ScriptBlock $comChecks -ArgumentList $file, $outDir, $pidFile
    try {
        if (Wait-Job -Job $job -Timeout $TimeoutSeconds) {
            $result = Receive-Job -Job $job -ErrorAction Continue | Select-Object -Last 1 |
                Select-Object -Property * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName
        }
        else {
            Stop-Job -Job $job
            $result = [pscustomobject]@{ file = $file; ok = $false; error = "Timed out after $TimeoutSeconds s (repair prompt or dialog?)." }
        }
    }
    finally {
        Remove-Job -Job $job -Force
        Stop-StartedPowerPoint -Before $before -PidFile $pidFile
        Remove-Item -LiteralPath $pidFile -ErrorAction SilentlyContinue
    }

    if ($null -eq $result) {
        $result = [pscustomobject]@{ file = $file; ok = $false; error = 'The PowerPoint job returned no result.' }
    }

    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outDir 'result.json') -Encoding utf8NoBOM
    $status = if ($result.ok) { 'PASS' } else { 'FAIL' }
    Write-Output "$status $file -> $outDir"
    if (-not $result.ok) {
        Write-Output ($result | ConvertTo-Json -Depth 5)
        $exitCode = 1
    }
}

exit $exitCode
