<#
.SYNOPSIS
  Sends a correctly signed test webhook to the relay hub, the same way GitHub or Stripe would.

.EXAMPLE
  ./scripts/send-test-webhook.ps1
  ./scripts/send-test-webhook.ps1 -Event order.created -Duplicate
  ./scripts/send-test-webhook.ps1 -BadSignature
#>
param(
    [string]$HubUrl = "http://localhost:5080",
    [string]$Source = "demo",
    [string]$Secret = "dev-demo-secret",
    [string]$Event = "order.created",
    [switch]$Duplicate,     # send the same delivery twice to show it is only stored once
    [switch]$BadSignature   # sign with the wrong secret to show it gets rejected
)

$deliveryId = [guid]::NewGuid().ToString()
$body = @{
    id       = "ord_" + (Get-Random -Maximum 99999)
    event    = $Event
    amount   = Get-Random -Minimum 5 -Maximum 500
    currency = "usd"
    sentAt   = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Compress

# HMAC-SHA256 over the exact bytes we send, as hex: this is the signature.
$key = [Text.Encoding]::UTF8.GetBytes($(if ($BadSignature) { "wrong-secret" } else { $Secret }))
$hmac = New-Object System.Security.Cryptography.HMACSHA256 (, $key)
$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$signature = -join ($hmac.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") })

$headers = @{
    "X-Signature-256" = "sha256=$signature"
    "X-Delivery-Id"   = $deliveryId
    "X-Event-Type"    = $Event
}

$times = if ($Duplicate) { 2 } else { 1 }
for ($i = 1; $i -le $times; $i++) {
    try {
        $response = Invoke-WebRequest -Uri "$HubUrl/webhooks/$Source" -Method Post -Body $bytes `
            -ContentType "application/json" -Headers $headers -UseBasicParsing
        Write-Host "Attempt $i -> $($response.StatusCode) $($response.Content)"
    } catch {
        Write-Host "Attempt $i -> $($_.Exception.Response.StatusCode.value__) (rejected)"
    }
}
