#Requires -Version 7
<#
.SYNOPSIS
    Builds templates/sample-monthly/sample-monthly.pptx with PowerPoint COM (docs/plans/pptx-poc-plan.md section 2).

.DESCRIPTION
    Requires Windows with PowerPoint installed. Refuses to run while any POWERPNT process exists, so it never
    attaches to the user's open documents. The PowerPoint process started by this script is always ended.

    After running, read the placeholders back from the .pptx (p:cSld/@name, p:ph/@idx, p:cNvPr/@name), update
    manifest.json if they changed, and set manifest template.sha256 to the SHA-256 of the written file.

.PARAMETER OutputPath
    Path of the .pptx to write. Defaults to sample-monthly.pptx next to this script.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot 'sample-monthly.pptx')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerPoint enums (msoTriState, PpPlaceholderType, MsoThemeFont, PpSaveAsFileType, MsoAutoSize).
$msoTrue = -1
$msoFalse = 0
$ppPlaceholderTitle = 1
$ppPlaceholderBody = 2
$ppPlaceholderCenterTitle = 3
$ppPlaceholderSubtitle = 4
$ppPlaceholderObject = 7
$ppPlaceholderTable = 12
$ppPlaceholderSlideNumber = 13
$ppPlaceholderFooter = 15
$ppPlaceholderDate = 16
$msoThemeLatin = 1
$msoThemeEastAsian = 3
$ppSaveAsOpenXMLPresentation = 24
$msoAutoSizeTextToFitShape = 2

$FooterText = 'WorkLens Sample Template'
$LatinFont = 'Calibri'
$EastAsianFont = 'Microsoft JhengHei'

# Default Office theme layout positions (1-based) kept by this template. Positions are used instead of names
# because a localized Office UI stores localized layout names.
$KeepLayouts = [ordered]@{ 1 = 'Title Slide'; 2 = 'Title and Content'; 4 = 'Two Content' }

function Get-FullWindowsPath([string] $Path) {
    # COM rejects forward slashes (Slide.Export, SaveAs); always pass a full backslash path.
    [IO.Path]::GetFullPath($Path).Replace('/', '\')
}

function New-LogoPng([string] $Path) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(240, 80)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 31, 78, 121))
            $graphics.FillRectangle($brush, 0, 0, 240, 80)
            $font = [System.Drawing.Font]::new('Arial', 28, [System.Drawing.FontStyle]::Bold)
            $format = [System.Drawing.StringFormat]::new()
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $format.LineAlignment = [System.Drawing.StringAlignment]::Center
            $graphics.DrawString('WorkLens', $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 0, 240, 80), $format)
            $font.Dispose(); $brush.Dispose(); $format.Dispose()
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

# Gives every placeholder on a master or layout an explicit English name (the file would otherwise store names in
# the Office UI language). Content-like placeholders are numbered by their position on the layout.
function Set-PlaceholderNames($Shapes) {
    $position = 0
    foreach ($shape in $Shapes) {
        $position++
        if ($shape.Type -ne 14) { continue } # msoPlaceholder
        $type = $shape.PlaceholderFormat.Type
        $prefix = switch ($type) {
            $ppPlaceholderTitle { 'Title' }
            $ppPlaceholderCenterTitle { 'Title' }
            $ppPlaceholderSubtitle { 'Subtitle' }
            $ppPlaceholderBody { 'Text Placeholder' }
            $ppPlaceholderObject { 'Content Placeholder' }
            $ppPlaceholderTable { 'Table Placeholder' }
            $ppPlaceholderDate { 'Date Placeholder' }
            $ppPlaceholderFooter { 'Footer Placeholder' }
            $ppPlaceholderSlideNumber { 'Slide Number Placeholder' }
            default { 'Placeholder' }
        }
        $shape.Name = "$prefix $position"
    }
}

function Set-FooterText($Shapes) {
    foreach ($shape in $Shapes) {
        if ($shape.Type -eq 14 -and $shape.PlaceholderFormat.Type -eq $ppPlaceholderFooter) {
            $shape.TextFrame.TextRange.Text = $FooterText
        }
    }
}

function Set-BodyAutofit($Shapes) {
    foreach ($shape in $Shapes) {
        if ($shape.Type -eq 14 -and $shape.PlaceholderFormat.Type -in @($ppPlaceholderBody, $ppPlaceholderObject)) {
            $shape.TextFrame2.AutoSize = $msoAutoSizeTextToFitShape
        }
    }
}

function Set-DocumentProperty($Presentation, [string] $Name, [string] $Value) {
    # Assigning .Value directly throws under PowerShell; late-bound InvokeMember works.
    $properties = $Presentation.BuiltInDocumentProperties
    $item = [System.__ComObject].InvokeMember('Item', [Reflection.BindingFlags]::GetProperty, $null, $properties, @($Name))
    [System.__ComObject].InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $item, @($Value)) | Out-Null
}

$outputFull = Get-FullWindowsPath $OutputPath
$logoPath = Get-FullWindowsPath (Join-Path ([IO.Path]::GetTempPath()) "worklens-logo-$PID.png")

if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    Write-Error 'PowerPoint is running. Close it first; this script does not attach to an existing PowerPoint.'
    exit 2
}

$app = $null
$startedPids = @()
$exitCode = 0
try {
    $app = New-Object -ComObject PowerPoint.Application
    $startedPids = @(Get-Process POWERPNT -ErrorAction SilentlyContinue | ForEach-Object Id)

    $presentation = $app.Presentations.Add($msoFalse)
    $master = $presentation.Designs.Item(1).SlideMaster

    # Theme fonts: Latin Calibri, East Asian Microsoft JhengHei, for both heading (major) and body (minor).
    foreach ($fontSet in @($master.Theme.ThemeFontScheme.MajorFont, $master.Theme.ThemeFontScheme.MinorFont)) {
        $fontSet.Item($msoThemeLatin).Name = $LatinFont
        $fontSet.Item($msoThemeEastAsian).Name = $EastAsianFont
    }

    # Delete the default layouts that are not kept, from the end so positions stay valid.
    for ($i = $master.CustomLayouts.Count; $i -ge 1; $i--) {
        if (-not $KeepLayouts.Contains($i)) { $master.CustomLayouts.Item($i).Delete() }
    }
    # Kept layouts are now at positions 1..3 in their original order.
    $index = 1
    foreach ($name in $KeepLayouts.Values) {
        $master.CustomLayouts.Item($index).Name = $name
        $index++
    }

    # Title and Table: a copy of Title and Content whose content placeholder is replaced by a table placeholder.
    $contentLayout = $master.CustomLayouts.Item(2)
    $tableLayout = $contentLayout.Duplicate()
    $tableLayout.Name = 'Title and Table'
    $object = $null
    foreach ($shape in $tableLayout.Shapes) {
        if ($shape.Type -eq 14 -and $shape.PlaceholderFormat.Type -eq $ppPlaceholderObject) { $object = $shape }
    }
    if ($null -eq $object) { throw 'Title and Table: no content placeholder to replace.' }
    $tableLayout.Shapes.AddPlaceholder($ppPlaceholderTable, $object.Left, $object.Top, $object.Width, $object.Height) | Out-Null
    $object.Delete()

    # Master elements: logo, footer text, slide number.
    New-LogoPng $logoPath
    $slideWidth = $presentation.PageSetup.SlideWidth
    $logoLeft = $slideWidth - 120 - 18
    $master.Shapes.AddPicture($logoPath, $msoFalse, $msoTrue, $logoLeft, 12, 120, 40).Name = 'Logo'
    # A title that wraps to two lines must not run under the logo (PoC P5): the master title ends 12 pt left of the
    # logo. The kept layouts' titles have no position of their own and inherit it.
    foreach ($shape in $master.Shapes) {
        if ($shape.Type -eq 14 -and $shape.PlaceholderFormat.Type -eq $ppPlaceholderTitle) {
            $shape.Width = $logoLeft - 12 - $shape.Left
        }
    }
    $master.HeadersFooters.Footer.Visible = $msoTrue
    $master.HeadersFooters.Footer.Text = $FooterText
    $master.HeadersFooters.SlideNumber.Visible = $msoTrue
    Set-PlaceholderNames $master.Shapes
    Set-FooterText $master.Shapes
    Set-BodyAutofit $master.Shapes

    foreach ($layout in $master.CustomLayouts) {
        $layout.HeadersFooters.Footer.Visible = $msoTrue
        $layout.HeadersFooters.Footer.Text = $FooterText
        $layout.HeadersFooters.SlideNumber.Visible = $msoTrue
        Set-PlaceholderNames $layout.Shapes
        Set-FooterText $layout.Shapes
        Set-BodyAutofit $layout.Shapes
    }

    # One demo slide (with notes) per layout. The renderer must remove them.
    $position = 1
    foreach ($layout in $master.CustomLayouts) {
        $slide = $presentation.Slides.AddSlide($position, $layout)
        foreach ($shape in $slide.Shapes) {
            if ($shape.HasTextFrame -eq $msoTrue) {
                $shape.TextFrame.TextRange.Text = "示範 Demo — $($layout.Name)"
            }
        }
        $slide.NotesPage.Shapes.Placeholders.Item(2).TextFrame.TextRange.Text = "示範備註 Demo notes ($($layout.Name))"
        $position++
    }

    $presentation.RemovePersonalInformation = $msoTrue
    foreach ($name in @('Author', 'Title', 'Last Author')) { Set-DocumentProperty $presentation $name '' }

    if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull }
    $presentation.SaveAs($outputFull, $ppSaveAsOpenXMLPresentation)
    $presentation.Close()
    Write-Output "Wrote $outputFull"
}
catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    $exitCode = 1
}
finally {
    # Release COM references before Quit, otherwise POWERPNT can stay alive after Quit.
    Get-Variable presentation, master, contentLayout, tableLayout, object, shape, layout, slide, fontSet -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Value = $null }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    if ($null -ne $app) {
        try { $app.Quit() } catch { Write-Warning "Quit failed: $($_.Exception.Message)" }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
        $app = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
    foreach ($id in $startedPids) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process) {
            $process | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            if (Get-Process -Id $id -ErrorAction SilentlyContinue) { Stop-Process -Id $id -Force }
        }
    }
    if (Test-Path -LiteralPath $logoPath) { Remove-Item -LiteralPath $logoPath }
}
exit $exitCode
