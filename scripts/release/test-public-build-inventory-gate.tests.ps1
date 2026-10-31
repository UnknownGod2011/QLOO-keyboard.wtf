$ErrorActionPreference = 'Stop'

# Source-contract and isolated, GUID-local inert inventory proof only. NEVER
# execute the build script, dotnet, Git, ISCC, installer, signing or app. These
# checks do not approve a policy or prove byte pinning through later packaging.
$sourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildPath = Join-Path $sourceRoot 'scripts\build-windows-release.ps1'
$inventoryPath = Join-Path $PSScriptRoot 'test-public-package-inventory.ps1'
$passed = 0

function Assert-BuildInventory {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "build_inventory_test_contract:$Message" }
}

function Get-PublicBuildInventoryContract {
    $text = [IO.File]::ReadAllText($buildPath)
    Assert-BuildInventory ($text.Length -le 1MB) 'source_size'
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
    Assert-BuildInventory ($errors.Count -eq 0) 'source_syntax'
    $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'PublicInventoryPolicy' })
    Assert-BuildInventory ($parameter.Count -eq 1) 'missing_public_policy_parameter'
    Assert-BuildInventory ($parameter[0].StaticType -eq [string]) 'policy_parameter_type'
    Assert-BuildInventory ($null -eq $parameter[0].DefaultValue) 'no_generated_policy_default'
    $blocks = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and
        $_.Clauses.Count -gt 0 -and $_.Clauses[0].Item1.Extent.Text -ceq '$PaidProductionRelease'
    })
    Assert-BuildInventory ($blocks.Count -eq 3) 'exact_public_blocks'
    $initial, $resolution, $validation = $blocks
    $versionCalls = @($initial.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and
        $node.Extent.Text.Contains("'scripts\release\test-public-release-versions.ps1'")
    }, $true))
    Assert-BuildInventory ($versionCalls.Count -eq 1) 'existing_version_gate'
    $versionStatement = $versionCalls[0].Parent
    while ($versionStatement -isnot [Management.Automation.Language.PipelineAst] -and $null -ne $versionStatement.Parent) {
        $versionStatement = $versionStatement.Parent
    }
    Assert-BuildInventory ($initial.Clauses[0].Item2.Statements[-1] -eq $versionStatement) 'version_last_after_policy_guard'
    Assert-BuildInventory ($initial.Extent.Text.Contains('[string]::IsNullOrWhiteSpace($PublicInventoryPolicy)')) 'nonblank_early_guard'
    Assert-BuildInventory ($initial.Extent.Text.Contains('public_inventory_policy_required')) 'required_guard_code'
    Assert-BuildInventory ($initial.Extent.Text.Contains('public_inventory_policy_nonpublic')) 'nonpublic_guard_code'
    Assert-BuildInventory ($initial.Clauses.Count -eq 2 -and $null -eq $initial.ElseClause) 'nonpublic_policy_refusal_branch'
    $initialText = $initial.Extent.Text.Replace($versionStatement.Extent.Text, '')

    $commands = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
    $effects = @($commands | Where-Object {
        $_.GetCommandName() -in @('git', 'dotnet', 'Remove-Item', 'New-Item') -or
        $_.Extent.Text -match '^&\s+\$iscc\b'
    })
    Assert-BuildInventory ($effects.Count -gt 0) 'known_build_effects'
    Assert-BuildInventory (@($effects | Where-Object { $_.Extent.StartOffset -lt $resolution.Extent.EndOffset }).Count -eq 0) 'all_preflights_before_effects'
    $publishAssignment = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$publishDir'
    })
    Assert-BuildInventory ($publishAssignment.Count -eq 1) 'exact_publish_path_assignment'
    $statements = @($ast.EndBlock.Statements)
    $publishIndex = [Array]::IndexOf($statements, $publishAssignment[0])
    Assert-BuildInventory ($statements[$publishIndex + 1] -eq $resolution) 'resolution_immediately_after_publish_path'
    Assert-BuildInventory ($resolution.Extent.Text.Contains('Get-Item') -and $resolution.Extent.Text.Contains('-LiteralPath')) 'literal_policy_resolution'
    Assert-BuildInventory ($resolution.Extent.Text.Contains('$publicInventoryPolicyPath') -and
        $resolution.Extent.Text.Contains('[IO.Path]::GetFullPath') -and
        $resolution.Extent.Text.Contains('ReparsePoint') -and
        $resolution.Extent.Text.Contains('1MB')) 'ordinary_bounded_canonical_policy'
    Assert-BuildInventory ($resolution.Extent.Text.Contains('public_inventory_policy_inside_publish')) 'cleanup_tree_guard'
    foreach ($command in @($resolution.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))) {
        Assert-BuildInventory ($command.GetCommandName() -in @('Get-Item', 'Join-Path', 'Test-Path', 'Resolve-Path')) 'resolution_unreviewed_command'
    }
    Assert-BuildInventory (@($resolution.FindAll({ param($node)
        $node -is [Management.Automation.Language.TryStatementAst]
    }, $true)).Count -le 1) 'resolution_shape'

    $publishCall = @($commands | Where-Object { $_.Extent.Text -ceq '& dotnet @publishArguments' })
    $compileCall = @($commands | Where-Object { $_.Extent.Text -ceq '& $iscc @isccArguments $installerScript' })
    Assert-BuildInventory ($publishCall.Count -eq 1 -and $compileCall.Count -eq 1) 'original_publish_compile_calls'
    Assert-BuildInventory ($publishCall[0].Extent.EndOffset -lt $validation.Extent.StartOffset -and
        $validation.Extent.EndOffset -lt $compileCall[0].Extent.StartOffset) 'inventory_after_publish_before_iscc'
    $publishFailure = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.StartOffset -gt $publishCall[0].Extent.EndOffset -and
        $_.Extent.EndOffset -lt $validation.Extent.StartOffset -and $_.Clauses[0].Item1.Extent.Text -ceq '$LASTEXITCODE -ne 0'
    })
    Assert-BuildInventory ($publishFailure.Count -eq 1 -and $publishFailure[0].Extent.Text.Contains('throw')) 'successful_publish_required'
    $dotSources = @($validation.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Dot
    }, $true))
    Assert-BuildInventory ($dotSources.Count -eq 1 -and
        $dotSources[0].Extent.Text.Contains("'scripts\release\test-public-package-inventory.ps1'")) 'actual_validator_dot_source'
    $inventoryCalls = @($validation.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Test-PublicPackageInventory'
    }, $true))
    Assert-BuildInventory ($inventoryCalls.Count -eq 1) 'actual_validator_once'
    Assert-BuildInventory ($inventoryCalls[0].Extent.Text -match
        '\ATest-PublicPackageInventory\s+-PackageDirectory\s+\$publishDir\s+-PolicyPath\s+\$publicInventoryPolicyPath\z') 'exact_inventory_inputs'
    $receiptAssignments = @($validation.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$publicInventoryReceipt'
    }, $true))
    Assert-BuildInventory ($receiptAssignments.Count -eq 1 -and
        $receiptAssignments[0].Extent.Text.Contains($inventoryCalls[0].Extent.Text)) 'original_receipt_assignment'
    Assert-BuildInventory ($validation.Extent.Text.Contains('$publicInventoryReceipt.policyInventoryMatched') -and
        $validation.Extent.Text.Contains('public_inventory_receipt_invalid')) 'truthful_receipt_required'
    Assert-BuildInventory (@($validation.FindAll({ param($node)
        $node -is [Management.Automation.Language.TryStatementAst]
    }, $true)).Count -eq 0) 'no_validation_catch_fallback'
    foreach ($command in @($validation.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))) {
        if ($command -eq $dotSources[0]) { continue }
        Assert-BuildInventory ($command.GetCommandName() -in @('Join-Path', 'Test-PublicPackageInventory', 'Write-Host')) 'validation_unreviewed_command'
        if ($command.GetCommandName() -ceq 'Write-Host') {
            Assert-BuildInventory ($command.Extent.Text -notmatch '\$(publishDir|PublicInventoryPolicy|publicInventoryPolicyPath)\b') 'content_free_inventory_summary'
        }
    }
    $validationText = $validation.Extent.Text.Replace($dotSources[0].Extent.Text, '')
    [pscustomobject]@{
        Initial = [scriptblock]::Create($initialText)
        Resolution = [scriptblock]::Create($resolution.Extent.Text)
        Validation = [scriptblock]::Create($validationText)
        Source = $text.Replace("`r`n", "`n")
    }
}

# Honest missing-parameter/wiring RED before ANY GUID/DPAPI/package resources.
$contract = Get-PublicBuildInventoryContract
. $inventoryPath

function Assert-Case {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action } catch { throw "Public inventory integration case failed: $Name. $($_.Exception.Message)" }
    $script:passed++
}

function Assert-Rejected {
    param([string]$Code, [scriptblock]$Action)
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception }
    Assert-BuildInventory ($null -ne $failure -and $failure.Message -ceq $Code) "expected_$Code"
    Assert-BuildInventory (-not $failure.Message.Contains('sensitive-fixture-value')) 'no_error_content'
}

function Invoke-IsolatedPreparation {
    param([string]$Policy, [string]$PublishDirectory, [bool]$Public = $true)
    $PaidProductionRelease = $Public
    $PublicInventoryPolicy = $Policy
    $publishDir = $PublishDirectory
    $publicInventoryPolicyPath = $null
    . $contract.Initial
    . $contract.Resolution
    return $publicInventoryPolicyPath
}

function Invoke-IsolatedValidation {
    param([string]$Policy, [string]$PublishDirectory, [hashtable]$CompileState, [scriptblock]$Compile)
    $PaidProductionRelease = $true
    $publicInventoryPolicyPath = $Policy
    $publishDir = $PublishDirectory
    $publicInventoryReceipt = $null
    . $contract.Validation
    # Synthetic compile callback ONLY: production contains no callback seam,
    # and this function never executes any part of the real compiler/build.
    if ($null -ne $Compile) { & $Compile $publicInventoryReceipt $CompileState }
    return $publicInventoryReceipt
}

function Invoke-InvalidReceipt {
    param($SyntheticReceipt, [bool]$ThrowInstead, [hashtable]$CompileState)
    function Test-PublicPackageInventory {
        param($PackageDirectory, $PolicyPath)
        if ($ThrowInstead) { throw [IO.IOException]::new('synthetic_inventory_validator_failure') }
        return $SyntheticReceipt
    }
    $PaidProductionRelease = $true
    $publishDir = 'inert-unopened-package'
    $publicInventoryPolicyPath = 'inert-unopened-policy'
    $publicInventoryReceipt = $null
    . $contract.Validation
    $CompileState.calls++
}

function Get-Hash {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-Fixture {
    $fixture = [pscustomobject]@{
        Root = Join-Path ([IO.Path]::GetTempPath()) ('kwt-build-inventory-' + [Guid]::NewGuid().ToString('N'))
        Package = $null
        Policy = $null
    }
    $fixture.Package = Join-Path $fixture.Root 'publish'
    $fixture.Policy = Join-Path $fixture.Root 'reviewed-policy.json'
    [IO.Directory]::CreateDirectory((Join-Path $fixture.Package 'browser-helper')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture.Package 'keyboard.wtf.exe'), 'inert-synthetic-application')
    [IO.File]::WriteAllText((Join-Path $fixture.Package 'browser-helper\manifest.json'), 'inert-synthetic-helper')
    Write-Policy $fixture
    return $fixture
}

function Write-Policy {
    param($Fixture)
    $policy = [ordered]@{
        schemaVersion = 1
        files = @(
            [ordered]@{ path = 'keyboard.wtf.exe'; required = $true; minBytes = 1; maxBytes = 128; sha256 = Get-Hash (Join-Path $Fixture.Package 'keyboard.wtf.exe') },
            [ordered]@{ path = 'browser-helper/manifest.json'; required = $true; minBytes = 1; maxBytes = 128; sha256 = Get-Hash (Join-Path $Fixture.Package 'browser-helper\manifest.json') }
        )
    }
    [IO.File]::WriteAllText($Fixture.Policy, ($policy | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Remove-Fixture {
    param($Fixture)
    $resolved = [IO.Path]::GetFullPath($Fixture.Root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    Assert-BuildInventory ([IO.Path]::GetDirectoryName($resolved) -ceq $temp -and
        [IO.Path]::GetFileName($resolved) -cmatch '\Akwt-build-inventory-[a-f0-9]{32}\z') 'owned_cleanup_root'
    if (-not [IO.Directory]::Exists($resolved)) { return }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $directories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    $leaves = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($resolved))
    $count = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-BuildInventory (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'cleanup_reparse_refusal'
        $directories.Add($directory)
        foreach ($item in $directory.EnumerateFileSystemInfos()) {
            $count++
            Assert-BuildInventory ($count -le 64 -and $item.FullName.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase)) 'bounded_cleanup_scope'
            Assert-BuildInventory (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'cleanup_reparse_refusal'
            if ($item -is [IO.DirectoryInfo]) { $pending.Push($item) }
            else { $leaves.Add($item) }
        }
    }
    # Validate the complete OWNED tiny tree before leaf-only deletion; never
    # recursive Remove-Item, cross-shell cleanup, or follow a reparse link.
    foreach ($leaf in $leaves) { $leaf.Delete() }
    for ($index = $directories.Count - 1; $index -ge 0; $index--) { $directories[$index].Delete() }
}

function With-Fixture {
    param([scriptblock]$Action)
    $fixture = New-Fixture
    try { & $Action $fixture } finally { Remove-Fixture $fixture }
}

function Assert-UnchangedSourceSlice {
    param([string]$Start, [string]$End, [string]$Hash, [bool]$IncludeEnd)
    $startOffset = $contract.Source.IndexOf($Start, [StringComparison]::Ordinal)
    Assert-BuildInventory ($startOffset -ge 0) 'protected_source_start'
    $endOffset = if ($null -eq $End -or $End.Length -eq 0) { $contract.Source.Length }
        else { $contract.Source.IndexOf($End, $startOffset, [StringComparison]::Ordinal) }
    Assert-BuildInventory ($endOffset -gt $startOffset) 'protected_source_end'
    if ($IncludeEnd) { $endOffset += $End.Length }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $observed = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($contract.Source.Substring($startOffset, $endOffset - $startOffset))))
        Assert-BuildInventory ($observed -ceq $Hash) 'candidate_or_legacy_body_changed'
    }
    finally { $sha.Dispose() }
}

Assert-Case 'Private and default publish path choices are unchanged' {
    Assert-UnchangedSourceSlice '$isSideBySide =' '$publishDir =' '167DF93C3780349568C6F0FE8CDB5A55413656391C941C0E0C603FE1E6AE3A36' $false
}
Assert-Case 'Existing dotnet publish flavor arguments are unchanged' {
    Assert-UnchangedSourceSlice '$publishArguments =' '& dotnet @publishArguments' 'E82EE87CB6AADECE10E3F361DDC14EB556233CA4904ED9E359F6B1CE3891CEB5' $true
}
Assert-Case 'Candidate installer arguments and original ISCC invocation are unchanged' {
    Assert-UnchangedSourceSlice '$isccArguments =' '& $iscc @isccArguments $installerScript' 'D7CBC532C3D1505975EDA7E3C33A8F42A8446C16C5B7AC77D3141388593CB7D4' $true
}
Assert-Case 'Legacy installer hashes and website side-effect policy are unchanged' {
    Assert-UnchangedSourceSlice '$installerName =' $null '90F3009B035BECCEA1C859E1B6991C411255131131E4EF77D355DDB4CD38E27D' $false
}
foreach ($empty in @($null, '', '   ')) {
    Assert-Case 'Missing public policy is refused without touching nonexistent inputs' {
        Assert-Rejected 'public_inventory_policy_required' { Invoke-IsolatedPreparation $empty 'inert-unopened-publish' }
    }
}
Assert-Case 'Private or legacy build cannot misleadingly consume a supplied public policy' {
    Assert-Rejected 'public_inventory_policy_nonpublic' { Invoke-IsolatedPreparation 'inert-unopened-policy' 'inert-unopened-publish' $false }
}
Assert-Case 'Private and legacy builds without policy remain inert' {
    Assert-BuildInventory ($null -eq (Invoke-IsolatedPreparation '' 'inert-unopened-publish' $false)) 'legacy_policy_side_effect'
}
Assert-Case 'Nonexistent policy refuses before package or compiler effects' {
    With-Fixture { param($fixture)
        Assert-Rejected 'public_inventory_policy_invalid' { Invoke-IsolatedPreparation (Join-Path $fixture.Root 'absent.json') $fixture.Package }
    }
}
Assert-Case 'A directory is not accepted as a policy file' {
    With-Fixture { param($fixture)
        Assert-Rejected 'public_inventory_policy_invalid' { Invoke-IsolatedPreparation $fixture.Root $fixture.Package }
    }
}
Assert-Case 'Empty policy refuses before package effects' {
    With-Fixture { param($fixture)
        [IO.File]::WriteAllBytes($fixture.Policy, [byte[]]::new(0))
        Assert-Rejected 'public_inventory_policy_invalid' { Invoke-IsolatedPreparation $fixture.Policy $fixture.Package }
    }
}
Assert-Case 'Oversized policy refuses before package effects' {
    With-Fixture { param($fixture)
        [IO.File]::WriteAllBytes($fixture.Policy, [byte[]]::new((1MB) + 1))
        Assert-Rejected 'public_inventory_policy_invalid' { Invoke-IsolatedPreparation $fixture.Policy $fixture.Package }
    }
}
Assert-Case 'Policy inside the publish cleanup tree is refused' {
    With-Fixture { param($fixture)
        $inside = Join-Path $fixture.Package 'reviewed-policy.json'
        [IO.File]::Copy($fixture.Policy, $inside)
        Assert-Rejected 'public_inventory_policy_inside_publish' { Invoke-IsolatedPreparation $inside $fixture.Package }
    }
}
Assert-Case 'Canonical normalization cannot hide policy inside the cleanup tree' {
    With-Fixture { param($fixture)
        $inside = Join-Path $fixture.Package 'reviewed-policy.json'
        [IO.File]::Copy($fixture.Policy, $inside)
        $aliased = Join-Path $fixture.Package '..\publish\reviewed-policy.json'
        Assert-Rejected 'public_inventory_policy_inside_publish' { Invoke-IsolatedPreparation $aliased $fixture.Package }
    }
}
Assert-Case 'Literal policy name with wildcard characters resolves the exact ordinary file' {
    With-Fixture { param($fixture)
        $literal = Join-Path $fixture.Root '[reviewed] policy.json'
        [IO.File]::Copy($fixture.Policy, $literal)
        Assert-BuildInventory ((Invoke-IsolatedPreparation $literal $fixture.Package) -ceq [IO.Path]::GetFullPath($literal)) 'literal_resolution'
    }
}
Assert-Case 'Malformed independently supplied policy cannot reach synthetic compiler' {
    With-Fixture { param($fixture)
        [IO.File]::WriteAllText($fixture.Policy, '{ malformed synthetic policy')
        $policy = Invoke-IsolatedPreparation $fixture.Policy $fixture.Package
        $compile = @{ calls = 0 }
        Assert-Rejected 'package_inventory_policy_invalid' { Invoke-IsolatedValidation $policy $fixture.Package $compile { param($receipt, $state) $state.calls++ } }
        Assert-BuildInventory ($compile.calls -eq 0) 'invalid_policy_compiled'
    }
}
foreach ($variant in @('extra', 'missing', 'mismatched')) {
    Assert-Case "Exact inventory $variant refusal cannot reach synthetic compiler" {
        With-Fixture { param($fixture)
            $expected = switch ($variant) {
                'extra' { [IO.File]::WriteAllText((Join-Path $fixture.Package 'private-diagnostic.txt'), 'sensitive-fixture-value'); 'package_inventory_extra_entry' }
                'missing' { [IO.File]::Delete((Join-Path $fixture.Package 'browser-helper\manifest.json')); 'package_inventory_required_missing' }
                'mismatched' { [IO.File]::WriteAllText((Join-Path $fixture.Package 'keyboard.wtf.exe'), 'different-inert-application'); 'package_inventory_hash_mismatch' }
            }
            $policy = Invoke-IsolatedPreparation $fixture.Policy $fixture.Package
            $compile = @{ calls = 0 }
            Assert-Rejected $expected { Invoke-IsolatedValidation $policy $fixture.Package $compile { param($receipt, $state) $state.calls++ } }
            Assert-BuildInventory ($compile.calls -eq 0) 'rejected_inventory_compiled'
        }
    }
}
Assert-Case 'Exact synthetic inventory preserves original receipt and inputs before one compiler callback' {
    With-Fixture { param($fixture)
        $paths = @($fixture.Policy, (Join-Path $fixture.Package 'keyboard.wtf.exe'), (Join-Path $fixture.Package 'browser-helper\manifest.json'))
        $before = @($paths | ForEach-Object { (Get-Hash $_) + ':' + [IO.File]::GetLastWriteTimeUtc($_).Ticks })
        $policy = Invoke-IsolatedPreparation $fixture.Policy $fixture.Package
        $compile = @{ calls = 0; receipt = $null }
        $receipt = Invoke-IsolatedValidation $policy $fixture.Package $compile { param($accepted, $state) $state.calls++; $state.receipt = $accepted }
        Assert-BuildInventory ($compile.calls -eq 1 -and [object]::ReferenceEquals($compile.receipt, $receipt)) 'exact_receipt_identity'
        Assert-BuildInventory ($receipt.policyInventoryMatched -and $receipt.policySha256 -ceq (Get-Hash $fixture.Policy) -and $receipt.fileCount -eq 2) 'actual_inventory_receipt'
        Assert-BuildInventory (-not $receipt.publicReleaseApproved -and -not $receipt.licensingCleared -and
            -not $receipt.postValidationBytesPinned -and -not $receipt.installerInclusionProven) 'no_public_ready_overclaim'
        $after = @($paths | ForEach-Object { (Get-Hash $_) + ':' + [IO.File]::GetLastWriteTimeUtc($_).Ticks })
        Assert-BuildInventory ([string]::Join('|', $before) -ceq [string]::Join('|', $after)) 'input_mutation'
    }
}
foreach ($invalid in @($null, @{ policyInventoryMatched = $false }, @{ unrelated = $true })) {
    Assert-Case 'Null false or wrong-shaped inventory receipt is not compiler permission' {
        $compile = @{ calls = 0 }
        Assert-Rejected 'public_inventory_receipt_invalid' { Invoke-InvalidReceipt $invalid $false $compile }
        Assert-BuildInventory ($compile.calls -eq 0) 'invalid_receipt_compiled'
    }
}
Assert-Case 'Unexpected validator exception cannot become compiler permission' {
    $compile = @{ calls = 0 }
    Assert-Rejected 'synthetic_inventory_validator_failure' { Invoke-InvalidReceipt $null $true $compile }
    Assert-BuildInventory ($compile.calls -eq 0) 'exception_fallback_compiled'
}

[pscustomobject][ordered]@{
    passed = $passed; failed = 0
    fixtureScope = 'AST-isolated public guards and GUID-local inert inventory only'
    realBuildExecuted = $false; installerExecuted = $false; packageApproved = $false
    postValidationBytesPinned = $false; installedAcceptanceProven = $false
} | ConvertTo-Json
