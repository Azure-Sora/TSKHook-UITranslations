param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw 'Invalid image version.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = 'Azure-Sora/TSKHook-UITranslations'
$package = Join-Path $root ".local/image-packages/$Version"
$manifest = Get-Content -LiteralPath (Join-Path $package 'image-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.downloadUrl -ne "https://github.com/$repo/releases/download/images-$Version/ui_images.zip") { throw 'Package version or repository mismatch.' }
$zip = Join-Path $package 'ui_images.zip'
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256 -or (Get-Item -LiteralPath $zip).Length -ne $manifest.size) { throw 'Archive checksum/size mismatch.' }
if ((git -C $root branch --show-current).Trim() -ne 'main' -or (git -C $root status --porcelain)) { throw 'Commit all reviewed changes on main before publication.' }
$origin = (git -C $root remote get-url origin).Trim().TrimEnd('/') -replace '\.git$', ''
if ($origin -ne "https://github.com/$repo" -and $origin -ne "git@github.com:$repo") { throw 'Origin mismatch.' }
$commit = (git -C $root rev-parse HEAD).Trim()
$tag = "images-$Version"
# Publish an immutable version first. Older plugins use GitHub latest for text.
gh release create $tag $zip (Join-Path $package 'image-manifest.json') --repo $repo --target $commit --draft --latest=false --title "UI images $Version" --notes "Independent UI image resources. Requires TSKHook 1.6.0. SHA-256: $($manifest.sha256)"
if ($LASTEXITCODE -ne 0) { throw 'Draft creation failed. Inspect GitHub before retrying.' }
gh release edit $tag --repo $repo --draft=false --latest=false
if ($LASTEXITCODE -ne 0) { throw 'Version publication failed; existing channel is unchanged.' }
# Update only the image manifest after its immutable archive is published.
gh release view image-channel --repo $repo *> $null
if ($LASTEXITCODE -ne 0) {
    gh release create image-channel (Join-Path $package 'image-manifest.json') --repo $repo --target $commit --latest=false --title 'Image resource update channel' --notes 'Manifest for independently versioned UI images.'
} else {
    gh release upload image-channel (Join-Path $package 'image-manifest.json') --repo $repo --clobber
}
if ($LASTEXITCODE -ne 0) { throw 'Image version published, but channel update failed. Retry only the channel upload after inspecting GitHub.' }
