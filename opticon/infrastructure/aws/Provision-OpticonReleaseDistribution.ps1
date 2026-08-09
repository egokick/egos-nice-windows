[CmdletBinding()]
param(
    [string]$StackName = "opticon-release-distribution",
    [string]$Region = "us-east-1"
)

$ErrorActionPreference = "Stop"
$expectedAccount = "053663732727"
$bucket = "opticon-053663732727"
$template = Join-Path $PSScriptRoot "opticon-release-distribution.yaml"

$identity = aws sts get-caller-identity --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $identity.Account -ne $expectedAccount) {
    throw "Refusing to provision Opticon releases outside AWS account $expectedAccount."
}

aws cloudformation deploy --region $Region --stack-name $StackName --template-file $template `
    --parameter-overrides "BucketName=$bucket" --no-fail-on-empty-changeset
if ($LASTEXITCODE -ne 0) { throw "CloudFormation deployment failed." }

$outputs = aws cloudformation describe-stacks --region $Region --stack-name $StackName --query "Stacks[0].Outputs" --output json | ConvertFrom-Json
$result = @{}
foreach ($output in $outputs) { $result[$output.OutputKey] = $output.OutputValue }

$versioning = aws s3api get-bucket-versioning --bucket $bucket --output json | ConvertFrom-Json
if ($versioning.Status -ne "Enabled") { throw "Bucket versioning is not enabled; immutable release recovery is unavailable." }
$publicAccess = aws s3api get-public-access-block --bucket $bucket --query "PublicAccessBlockConfiguration" --output json | ConvertFrom-Json
foreach ($property in "BlockPublicAcls", "IgnorePublicAcls", "BlockPublicPolicy", "RestrictPublicBuckets") {
    if ($publicAccess.$property -ne $true) { throw "S3 Block Public Access $property is not enabled." }
}
[pscustomobject]@{
    Account = $identity.Account
    Bucket = $result.BucketName
    DistributionId = $result.DistributionId
    DistributionDomain = $result.DistributionDomainName
}
