[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$revision = '83b26348c811f072429427ff48362238656524fe'
$destination = Join-Path $root 'src/windows/RemoteDisplaySwitch.Host/Assets/Fluent'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$icons = @(
    @{ Name = 'Desktop'; Slug = 'desktop'; Size = 20 },
    @{ Name = 'Desktop Off'; Slug = 'desktop_off'; Size = 20 },
    @{ Name = 'Question Circle'; Slug = 'question_circle'; Size = 20 },
    @{ Name = 'Power'; Slug = 'power'; Size = 20 },
    @{ Name = 'Settings'; Slug = 'settings'; Size = 20 },
    @{ Name = 'Arrow Left'; Slug = 'arrow_left'; Size = 20 },
    @{ Name = 'Arrow Sync'; Slug = 'arrow_sync'; Size = 20 },
    @{ Name = 'Checkmark'; Slug = 'checkmark'; Size = 20 },
    @{ Name = 'Phone'; Slug = 'phone'; Size = 20 },
    @{ Name = 'Save'; Slug = 'save'; Size = 20 },
    @{ Name = 'Scan'; Slug = 'scan'; Size = 20 },
    @{ Name = 'Link'; Slug = 'link'; Size = 20 },
    @{ Name = 'Link Dismiss'; Slug = 'link_dismiss'; Size = 20 },
    @{ Name = 'Copy'; Slug = 'copy'; Size = 20 },
    @{ Name = 'Shield Dismiss'; Slug = 'shield_dismiss'; Size = 20 },
    @{ Name = 'Dismiss'; Slug = 'dismiss'; Size = 16 },
    @{ Name = 'Subtract'; Slug = 'subtract'; Size = 16 },
    @{ Name = 'Square'; Slug = 'square'; Size = 16 },
    @{ Name = 'Square Multiple'; Slug = 'square_multiple'; Size = 16 }
)
foreach ($icon in $icons) {
    $filename = 'ic_fluent_{0}_{1}_regular.svg' -f $icon.Slug, $icon.Size
    $url = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{0}/assets/{1}/SVG/{2}' -f $revision, [Uri]::EscapeDataString($icon.Name), $filename
    $file = Join-Path $destination $filename
    Invoke-WebRequest -Uri $url -OutFile $file -TimeoutSec 30
    [xml]$svg = Get-Content -LiteralPath $file -Raw
    if ($svg.DocumentElement.LocalName -ne 'svg' -or @($svg.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -ne 'path' }).Count) {
        throw "Unsupported SVG content: $filename"
    }
}
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/$revision/LICENSE" -OutFile (Join-Path $root 'licenses/Fluent-System-Icons.LICENSE') -TimeoutSec 30
Write-Output "Imported $($icons.Count) Windows icons from $revision."
