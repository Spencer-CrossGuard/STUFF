Set-StrictMode -Version Latest

function Read-BHSharpHoundEntry {
    param([scriptblock] $OpenStream, [string] $Name, $Importer, $Quotas, $Skipped, $Versions)

    $stream = & $OpenStream
    try {
        $json = [BH.Native.SharpHoundImport]::SkipUtf8Bom($stream)
        $reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader($json, $Quotas)
        try { $meta = [BH.Native.SharpHoundImport]::ReadMetadata($reader) }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }

    [void]$Versions.Add($meta.Version)
    if (-not [BH.Native.SharpHoundImport]::Supports($meta.Type)) {
        [void]$Skipped.Add($meta.Type)
        return
    }
    if ($meta.Version -notin 5, 6) { throw "Unsupported SharpHound schema $($meta.Version) in '$Name'. Expected version 5 or 6." }

    $stream = & $OpenStream
    try {
        $json = [BH.Native.SharpHoundImport]::SkipUtf8Bom($stream)
        $reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader($json, $Quotas)
        try { $Importer.ReadRecords($reader, $meta) }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Start-BHSharpHoundImport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]] $InputPaths,
        [Parameter(Mandatory)][string] $OutputRoot,
        [scriptblock] $ProgressCallback
    )

    Initialize-BHNative
    Add-Type -AssemblyName System.Runtime.Serialization
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $inputs = @(foreach ($path in $InputPaths) {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item -isnot [IO.FileInfo] -or $item.Extension -notin '.json', '.zip') {
            throw "Expected a SharpHound ZIP or JSON file: '$path'."
        }
        $item
    })

    $root = (New-Item -ItemType Directory -Path $OutputRoot -Force).FullName
    $id = "sharphound-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $temporary = (New-Item -ItemType Directory -Path (Join-Path $root ".$id.tmp")).FullName
    $destination = Join-Path $root $id
    $skipped = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $versions = [System.Collections.Generic.HashSet[int]]::new()
    $quotas = [System.Xml.XmlDictionaryReaderQuotas]::new()
    $quotas.MaxDepth = 64
    $quotas.MaxStringContentLength = 1048576
    $quotas.MaxArrayLength = 1048576
    $quotas.MaxNameTableCharCount = 1048576
    try {
        $writer = [BH.Native.SharpHoundImport]::new($temporary)
        $entryOptions = @{ Importer = $writer; Quotas = $quotas; Skipped = $skipped; Versions = $versions }
        try {
            foreach ($inputFile in $inputs) {
                if ($inputFile.Extension -ieq '.json') {
                    if ($ProgressCallback) { & $ProgressCallback "Importing $($inputFile.Name)" }
                    $path = $inputFile.FullName
                    Read-BHSharpHoundEntry -OpenStream { [IO.File]::OpenRead($path) } -Name $inputFile.Name @entryOptions
                    continue
                }
                $archive = [IO.Compression.ZipFile]::OpenRead($inputFile.FullName)
                try {
                    $jsonEntries = @($archive.Entries | Where-Object { $_.Name -like '*.json' })
                    if ($jsonEntries.Count -eq 0) { throw "No SharpHound JSON files found inside '$($inputFile.Name)'." }
                    foreach ($entry in $jsonEntries) {
                        if ($ProgressCallback) { & $ProgressCallback "Importing $($inputFile.Name) / $($entry.Name)" }
                        Read-BHSharpHoundEntry -OpenStream { $entry.Open() } -Name $entry.Name @entryOptions
                    }
                }
                finally { $archive.Dispose() }
            }
            if ($writer.Nodes -eq 0) { throw 'No supported, live SharpHound objects were found.' }
        }
        finally { $writer.Dispose() }

        $notes = @('SharpHound import includes selected relationships. BloodHound post-processing for AD CS, trust paths and other derived edges is not reproduced.')
        if ($skipped.Count) { $notes += "Skipped SharpHound object types: $(@($skipped | Sort-Object) -join ', ')." }
        $meta = [ordered]@{
            ScanId = $id
            Status = 'Complete'
            FinishedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Source = 'SharpHound'
            SourceFiles = @($inputs | ForEach-Object FullName)
            SchemaVersions = @($versions | Sort-Object)
            Counts = @{ Objects = $writer.Nodes; Edges = $writer.Edges }
            CoverageNotes = $notes
            Errors = 0
        }
        $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $temporary 'scan-metadata.json') -Encoding UTF8
        [IO.Directory]::Move($temporary, $destination)
        return $destination
    }
    finally {
        if ([IO.Directory]::Exists($temporary)) {
            foreach ($file in [IO.Directory]::EnumerateFiles($temporary)) { [IO.File]::Delete($file) }
            [IO.Directory]::Delete($temporary)
        }
    }
}

Export-ModuleMember -Function Start-BHSharpHoundImport
