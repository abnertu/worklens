#Requires -Version 7
<#
.SYNOPSIS
    Builds templates/rd-monthly/rd-monthly.pptx with PowerPoint COM (docs/design/report-template.md sections 2 to 8).

.DESCRIPTION
    Requires Windows with PowerPoint installed. Refuses to run while any POWERPNT process exists, so it never
    attaches to the user's open documents. PowerPoint runs one instance, so do not open PowerPoint while the script
    runs: a file opened then belongs to this instance. The script then leaves PowerPoint running, warns and exits 1,
    so that the user's work is never closed. Otherwise the PowerPoint process started by this script is always
    ended. The old template is replaced only after the new one is saved.

    Each slot of templates/rd-monthly/manifest.json is a named placeholder. Sizes and colors follow the design:
    positions are the design px times 0.75 (pt). After running, set manifest template.sha256 to the SHA-256 of the
    written file.

.PARAMETER OutputPath
    Path of the .pptx to write. Defaults to rd-monthly.pptx next to this script.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot 'rd-monthly.pptx')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerPoint enums (msoTriState, PpPlaceholderType, MsoThemeFont, PpSaveAsFileType, alignment, shapes).
$msoTrue = -1
$msoFalse = 0
$ppPlaceholderTitle = 1
$ppPlaceholderBody = 2
$ppPlaceholderTable = 12
$ppPlaceholderSlideNumber = 13
$ppPlaceholderFooter = 15
$ppPlaceholderDate = 16
$ppPlaceholderPicture = 18
$msoThemeLatin = 1
$msoThemeEastAsian = 3
$ppSaveAsOpenXMLPresentation = 24
$ppAlignLeft = 1
$ppAlignCenter = 2
$ppAlignRight = 3
$msoAnchorTop = 1
$msoAnchorMiddle = 3
$msoAutoSizeNone = 0
$msoAutoSizeTextToFitShape = 2
$msoShapeRectangle = 1
$msoShapeRoundedRectangle = 5

$LatinFont = 'Segoe UI'
$EastAsianFont = 'Microsoft JhengHei'

# Color tokens (design section 4).
$Colors = @{
    ink = '#111827'; body = '#1F2937'; muted = '#4B5563'; subtle = '#6B7280'; line = '#E5E7EB'
    surface = '#F6F7F9'; paper = '#FFFFFF'; accent = '#1E3A8A'; neutralSoft = '#F3F4F6'
    onAccent = '#FFFFFF'; onAccentMuted = '#C7D2FE'
}

# Type sizes in pt (design section 3).
$Size = @{ display = 54; title = 24; subtitle = 18; heading = 13.5; body = 13.5; meta = 12; tag = 10.5; footer = 9 }

function Get-FullWindowsPath([string] $Path) {
    # COM rejects forward slashes (SaveAs); always pass a full backslash path.
    [IO.Path]::GetFullPath($Path).Replace('/', '\')
}

# COM colors are BGR integers.
function ConvertTo-Bgr([string] $Hex) {
    $r = [Convert]::ToInt32($Hex.Substring(1, 2), 16)
    $g = [Convert]::ToInt32($Hex.Substring(3, 2), 16)
    $b = [Convert]::ToInt32($Hex.Substring(5, 2), 16)
    $r + 256 * $g + 65536 * $b
}

# COM starts PowerPoint with /automation -Embedding. A window that the user opens has neither, so it is never ended.
function Get-ComPowerPointId {
    @(Get-CimInstance Win32_Process -Filter "Name = 'POWERPNT.EXE'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '/automation|-Embedding' } | ForEach-Object ProcessId)
}

# Text format of a placeholder: inherited by every slide that uses the layout.
function Set-TextFormat($Shape, [double] $FontSize, [string] $Color, [bool] $Bold = $false, [int] $Align = $ppAlignLeft,
    [int] $Anchor = $msoAnchorTop, [bool] $Bullets = $false) {
    $range = $Shape.TextFrame.TextRange
    $range.Font.Size = $FontSize
    $range.Font.Bold = if ($Bold) { $msoTrue } else { $msoFalse }
    $range.Font.Color.RGB = ConvertTo-Bgr $Color
    $range.ParagraphFormat.Alignment = $Align
    $range.ParagraphFormat.Bullet.Visible = if ($Bullets) { $msoTrue } else { $msoFalse }
    $Shape.TextFrame.VerticalAnchor = $Anchor
    # No shrink to fit: Layout truncates to max_chars with an ellipsis, so text keeps the design sizes.
    $Shape.TextFrame2.AutoSize = $msoAutoSizeNone
}

function Set-Fill($Shape, [string] $Color) {
    $Shape.Fill.Visible = $msoTrue
    $Shape.Fill.Solid()
    $Shape.Fill.ForeColor.RGB = ConvertTo-Bgr $Color
}

# Adds a named placeholder at a position in pt.
function Add-Placeholder($Layout, [int] $Type, [string] $Name, [double[]] $Box) {
    $shape = $Layout.Shapes.AddPlaceholder($Type, $Box[0], $Box[1], $Box[2], $Box[3])
    $shape.Name = $Name
    $shape
}

function Add-Text($Layout, [string] $Name, [double[]] $Box, [double] $FontSize, [string] $Color, [bool] $Bold = $false,
    [int] $Align = $ppAlignLeft, [int] $Anchor = $msoAnchorTop) {
    $shape = Add-Placeholder $Layout $ppPlaceholderBody $Name $Box
    Set-TextFormat $shape $FontSize $Color $Bold $Align $Anchor $false
    $shape
}

function Add-Bullets($Layout, [string] $Name, [double[]] $Box, [double] $FontSize = $Size.body) {
    $shape = Add-Placeholder $Layout $ppPlaceholderBody $Name $Box
    Set-TextFormat $shape $FontSize $Colors.body $false $ppAlignLeft $msoAnchorTop $true
    $shape
}

# A 2 px accent line under a block heading (design section 6).
function Add-HeadingRule($Layout, [string] $Heading, [double[]] $Box) {
    $y = $Box[1] + $Box[3]
    $line = $Layout.Shapes.AddLine($Box[0], $y, $Box[0] + $Box[2], $y)
    $line.Line.ForeColor.RGB = ConvertTo-Bgr $Colors.accent
    $line.Line.Weight = 1.5
    $line.Name = "$Heading Rule"
}

# The title placeholder of a layout, moved to a box and formatted.
function Set-Title($Layout, [double[]] $Box, [double] $FontSize = $Size.title, [int] $Align = $ppAlignLeft, [string] $Color = $Colors.ink) {
    foreach ($shape in $Layout.Shapes) {
        if ($shape.Type -eq 14 -and $shape.PlaceholderFormat.Type -eq $ppPlaceholderTitle) {
            $shape.Name = 'Title'
            $shape.Left = $Box[0]; $shape.Top = $Box[1]; $shape.Width = $Box[2]; $shape.Height = $Box[3]
            Set-TextFormat $shape $FontSize $Color $true $Align $msoAnchorMiddle $false
            return
        }
    }
    throw "Layout $($Layout.Name) has no title placeholder."
}

# A new layout with the title, footer and slide number of the master, without the date.
function New-Layout($Master, [string] $Name, [bool] $Footer = $true) {
    $layout = $Master.CustomLayouts.Add($Master.CustomLayouts.Count + 1)
    $layout.Name = $Name
    for ($i = $layout.Shapes.Count; $i -ge 1; $i--) {
        $shape = $layout.Shapes.Item($i)
        if ($shape.Type -ne 14) { continue }
        switch ($shape.PlaceholderFormat.Type) {
            $ppPlaceholderTitle { }
            # The renderer copies every footer and slide number placeholder of the layout, so a layout without a
            # footer must not have them at all.
            $ppPlaceholderFooter { if ($Footer) { $shape.Name = 'Footer' } else { $shape.Delete() } }
            $ppPlaceholderSlideNumber { if ($Footer) { $shape.Name = 'Slide Number' } else { $shape.Delete() } }
            default { $shape.Delete() }
        }
    }
    $layout
}

function Add-FooterLogo($Layout) {
    Add-Placeholder $Layout $ppPlaceholderPicture 'Footer Logo' @(816, 501, 54, 15) | Out-Null
}

function Set-DocumentProperty($Presentation, [string] $Name, [string] $Value) {
    # Assigning .Value directly throws under PowerShell; late-bound InvokeMember works.
    $properties = $Presentation.BuiltInDocumentProperties
    $item = [System.__ComObject].InvokeMember('Item', [Reflection.BindingFlags]::GetProperty, $null, $properties, @($Name))
    [System.__ComObject].InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $item, @($Value)) | Out-Null
}

$outputFull = Get-FullWindowsPath $OutputPath
# SaveAs writes here first, so that a failed save leaves the old template as it was.
$tempFull = Join-Path ([IO.Path]::GetDirectoryName($outputFull)) "~build-$PID.pptx"

if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    Write-Error 'PowerPoint is running. Close it first; this script does not attach to an existing PowerPoint.'
    exit 2
}

$app = $null
$startedPids = @()
$ownOpen = $false
$exitCode = 0
try {
    $app = New-Object -ComObject PowerPoint.Application
    $startedPids = @(Get-ComPowerPointId)

    $presentation = $app.Presentations.Add($msoFalse)
    $ownOpen = $true
    $presentation.PageSetup.SlideWidth = 960
    $presentation.PageSetup.SlideHeight = 540
    $master = $presentation.Designs.Item(1).SlideMaster

    foreach ($fontSet in @($master.Theme.ThemeFontScheme.MajorFont, $master.Theme.ThemeFontScheme.MinorFont)) {
        $fontSet.Item($msoThemeLatin).Name = $LatinFont
        $fontSet.Item($msoThemeEastAsian).Name = $EastAsianFont
    }

    # Master: the content grid (design section 5), the footer and the page number.
    $master.Background.Fill.Solid()
    $master.Background.Fill.ForeColor.RGB = ConvertTo-Bgr $Colors.paper
    foreach ($shape in @($master.Shapes)) {
        if ($shape.Type -ne 14) { continue }
        switch ($shape.PlaceholderFormat.Type) {
            $ppPlaceholderTitle {
                $shape.Name = 'Title'
                $shape.Left = 48; $shape.Top = 36; $shape.Width = 864; $shape.Height = 33
                Set-TextFormat $shape $Size.title $Colors.ink $true $ppAlignLeft $msoAnchorMiddle $false
            }
            $ppPlaceholderBody {
                $shape.Name = 'Body'
                $shape.Left = 48; $shape.Top = 90; $shape.Width = 864; $shape.Height = 390
                Set-TextFormat $shape $Size.body $Colors.body $false $ppAlignLeft $msoAnchorTop $true
            }
            $ppPlaceholderFooter {
                $shape.Name = 'Footer'
                $shape.Left = 48; $shape.Top = 501; $shape.Width = 600; $shape.Height = 15
                $shape.TextFrame.TextRange.Text = ''
                Set-TextFormat $shape $Size.footer $Colors.subtle $false $ppAlignLeft $msoAnchorMiddle $false
            }
            $ppPlaceholderSlideNumber {
                $shape.Name = 'Slide Number'
                $shape.Left = 876; $shape.Top = 501; $shape.Width = 36; $shape.Height = 15
                Set-TextFormat $shape $Size.footer $Colors.subtle $false $ppAlignRight $msoAnchorMiddle $false
            }
            $ppPlaceholderDate { $shape.Delete() }
        }
    }
    $rule = $master.Shapes.AddLine(48, 492, 912, 492)
    $rule.Line.ForeColor.RGB = ConvertTo-Bgr $Colors.line
    $rule.Line.Weight = 0.75
    $rule.Name = 'Footer Rule'
    $master.HeadersFooters.Footer.Visible = $msoTrue
    $master.HeadersFooters.Footer.Text = ''
    $master.HeadersFooters.SlideNumber.Visible = $msoTrue
    $master.HeadersFooters.DateAndTime.Visible = $msoFalse

    $defaultLayouts = $master.CustomLayouts.Count

    # Cover (design section 8.1). The brand band covers the right side, so the master shapes stay hidden.
    $cover = New-Layout $master 'Cover' $false
    $cover.DisplayMasterShapes = $msoFalse
    $band = $cover.Shapes.AddShape($msoShapeRectangle, 672, 0, 288, 540)
    Set-Fill $band $Colors.accent
    $band.Line.Visible = $msoFalse
    $band.Name = 'Brand Band'
    $kickerRule = $cover.Shapes.AddLine(60, 78, 84, 78)
    $kickerRule.Line.ForeColor.RGB = ConvertTo-Bgr $Colors.accent
    $kickerRule.Line.Weight = 1.5
    $kickerRule.Name = 'Kicker Rule'
    Add-Text $cover 'Kicker' @(60, 54, 540, 18) $Size.meta $Colors.muted | Out-Null
    Set-Title $cover @(60, 150, 540, 66) $Size.display
    Add-Text $cover 'Subtitle' @(60, 228, 540, 24) $Size.subtitle $Colors.body | Out-Null
    Add-Text $cover 'Subject' @(60, 390, 540, 21) $Size.heading $Colors.ink $true | Out-Null
    Add-Text $cover 'Meta' @(60, 417, 540, 18) $Size.meta $Colors.muted | Out-Null
    Add-Text $cover 'Unit Name' @(60, 471, 450, 18) $Size.meta $Colors.muted | Out-Null
    Add-Placeholder $cover $ppPlaceholderPicture 'Logo' @(510, 465, 90, 30) | Out-Null
    Add-Text $cover 'Band Year' @(702, 54, 150, 18) $Size.meta $Colors.onAccentMuted | Out-Null
    Add-Text $cover 'Band Month' @(702, 438, 150, 66) $Size.display $Colors.onAccent $true | Out-Null

    # Summary (design section 8.2). The metric cards carry their own fill, so an unbound card leaves no empty box.
    $summary = New-Layout $master 'Summary'
    Set-Title $summary @(48, 36, 560, 33)
    Add-Text $summary 'Period' @(612, 36, 300, 33) $Size.meta $Colors.muted $false $ppAlignRight $msoAnchorMiddle | Out-Null
    $cardWidth = (864 - 3 * 15) / 4
    $cards = @('Metric Completed', 'Metric In Progress', 'Metric Operations', 'Metric Projects')
    for ($i = 0; $i -lt $cards.Count; $i++) {
        $card = Add-Text $summary $cards[$i] @((48 + $i * ($cardWidth + 15)), 90, $cardWidth, 90) $Size.meta $Colors.muted
        Set-Fill $card $Colors.surface
    }
    $highlightsTitle = @(48, 210, 516, 24)
    Add-Text $summary 'Highlights Title' $highlightsTitle $Size.heading $Colors.ink $true | Out-Null
    Add-HeadingRule $summary 'Highlights Title' $highlightsTitle
    Add-Bullets $summary 'Highlights' @(48, 243, 516, 180) | Out-Null
    # The support block (594,210,318,213) is one filled area: its heading and its items share the fill. The styles
    # support_title and support switch both to warn-soft when there is support.
    $supportTitle = Add-Text $summary 'Support Title' @(594, 210, 318, 30) $Size.heading $Colors.ink $true $ppAlignLeft $msoAnchorMiddle
    Set-Fill $supportTitle $Colors.surface
    $support = Add-Bullets $summary 'Support' @(594, 240, 318, 183)
    Set-Fill $support $Colors.surface
    Add-Text $summary 'Activity Note' @(48, 450, 864, 18) $Size.meta $Colors.muted | Out-Null
    Add-FooterLogo $summary

    # Table (design sections 8.3, 8.5 and 8.6): achievements, operations and the challenge table.
    $table = New-Layout $master 'Table'
    Set-Title $table @(48, 36, 560, 33)
    Add-Text $table 'Status' @(612, 36, 300, 33) $Size.meta $Colors.muted $false $ppAlignRight $msoAnchorMiddle | Out-Null
    Add-Placeholder $table $ppPlaceholderTable 'Items' @(48, 90, 864, 390) | Out-Null
    Add-FooterLogo $table

    # Detail (design section 8.4). The status tag beside the title keeps rounded corners.
    $detail = New-Layout $master 'Detail'
    Add-Text $detail 'Kicker' @(48, 36, 864, 15) $Size.meta $Colors.muted | Out-Null
    Set-Title $detail @(48, 54, 750, 33)
    $tag = Add-Text $detail 'Status' @(810, 60, 102, 21) $Size.tag $Colors.muted $false $ppAlignCenter $msoAnchorMiddle
    $tag.AutoShapeType = $msoShapeRoundedRectangle
    Set-Fill $tag $Colors.neutralSoft
    $factsTitle = @(48, 111, 480, 24)
    Add-Text $detail 'Facts Title' $factsTitle $Size.heading $Colors.ink $true | Out-Null
    Add-HeadingRule $detail 'Facts Title' $factsTitle
    Add-Bullets $detail 'Facts' @(48, 144, 480, 336) | Out-Null
    $evidenceTitle = @(564, 111, 348, 24)
    Add-Text $detail 'Evidence Title' $evidenceTitle $Size.heading $Colors.ink $true | Out-Null
    Add-HeadingRule $detail 'Evidence Title' $evidenceTitle
    Add-Bullets $detail 'Evidence' @(564, 144, 348, 300) $Size.meta | Out-Null
    Add-FooterLogo $detail

    # Narrative (design section 8.7).
    $narrative = New-Layout $master 'Narrative'
    Set-Title $narrative @(48, 36, 864, 33)
    Add-Bullets $narrative 'Body' @(48, 90, 864, 390) | Out-Null
    Add-FooterLogo $narrative

    # Closing.
    $closing = New-Layout $master 'Closing'
    Set-Title $closing @(48, 210, 864, 80) $Size.title $ppAlignCenter

    # The default layouts of the blank presentation are not part of the template.
    for ($i = $defaultLayouts; $i -ge 1; $i--) { $master.CustomLayouts.Item($i).Delete() }

    $presentation.RemovePersonalInformation = $msoTrue
    foreach ($name in @('Author', 'Title', 'Last Author')) { Set-DocumentProperty $presentation $name '' }

    $presentation.SaveAs($tempFull, $ppSaveAsOpenXMLPresentation)
    $presentation.Close()
    $ownOpen = $false
    Move-Item -LiteralPath $tempFull -Destination $outputFull -Force
    Write-Output "Wrote $outputFull"
}
catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    $exitCode = 1
}
finally {
    if ($ownOpen) {
        try { $presentation.Close(); $ownOpen = $false } catch { Write-Warning "Close failed: $($_.Exception.Message)" }
    }
    # Release COM references before Quit, otherwise POWERPNT can stay alive after Quit.
    Get-Variable presentation, master, supportTitle, fontSet, shape, rule, cover, band, kickerRule, summary, card, support, table, detail, tag, narrative, closing, layout -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Value = $null }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    # PowerPoint runs one instance: a file that the user opens during the run opens in this one. Quit only when no
    # other presentation is open, so that the user's work is never closed.
    $keep = $false
    if ($null -ne $app) {
        # Our own presentation counts when it could not be closed; Quit closes it. A failed check keeps PowerPoint,
        # because a dialog on the user's file can make PowerPoint reject the call.
        try { $keep = $app.Presentations.Count - $(if ($ownOpen) { 1 } else { 0 }) -gt 0 }
        catch {
            Write-Warning "Presentation check failed: $($_.Exception.Message)"
            $keep = $true
        }
        if ($keep) {
            Write-Warning 'PowerPoint has another open presentation; it is left running. Do not open PowerPoint while this script runs.'
            $exitCode = 1
        }
        else {
            try { $app.Quit() } catch { Write-Warning "Quit failed: $($_.Exception.Message)" }
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
        $app = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
    # New-Object can fail after PowerPoint starts; no PowerPoint ran before this script, so a COM one is its own.
    if (-not $keep -and $startedPids.Count -eq 0) { $startedPids = @(Get-ComPowerPointId) }
    foreach ($id in $(if ($keep) { @() } else { $startedPids })) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process) {
            $process | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            if (Get-Process -Id $id -ErrorAction SilentlyContinue) { Stop-Process -Id $id -Force }
        }
    }
    # The temporary file stays locked when PowerPoint is kept running; it is then left for the user to remove.
    if (Test-Path -LiteralPath $tempFull) {
        try { Remove-Item -LiteralPath $tempFull } catch { Write-Warning "Cannot remove ${tempFull}: $($_.Exception.Message)" }
    }
}
exit $exitCode
