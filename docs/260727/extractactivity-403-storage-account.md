
All commands


DNS resolution (does the private zone answer?)

# PowerShell — goes through the real OS resolver path, most representative
Resolve-DnsName corstdatacapdevwe.blob.core.windows.net

# CMD — bypasses the OS resolver, queries a DNS server directly; useful as a
# second data point but don't treat it as authoritative over Resolve-DnsName
nslookup corstdatacapdevwe.blob.core.windows.net

# PowerShell — confirms which DNS server the app is actually configured to use
Get-DnsClientServerAddress

# CMD/PowerShell — same info plus adapter/route details; look at "DNS Servers"
ipconfig /all

Reachability / TLS to the resolved IP (is the connection actually happening?)

# PowerShell — TCP-level check against port 443, works even without a real HTTP call
Test-NetConnection corstdatacapdevwe.blob.core.windows.net -Port 443

# PowerShell — same check pinned to the specific IP nslookup/Resolve-DnsName returned,
# to confirm you're actually hitting the address you think you are
Test-NetConnection 135.130.102.1 -Port 443

Route path (is traffic leaving via VNet integration or some other egress?)

# CMD/PowerShell
tracert corstdatacapdevwe.blob.core.windows.net
tracert 10.243.4.x   # if/when it does resolve privately, confirm the route stays internal

Auth/token proof (is it really network, not RBAC?)

# CMD/PowerShell — get a managed-identity token from IMDS
curl -s -H "Metadata: true" "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2019-08-01&resource=https://storage.azure.com/"

# Then call the storage account directly with that token
curl -s -i -H "Authorization: Bearer <token>" -H "x-ms-version: 2021-08-06" "https://corstdatacapdevwe.blob.core.windows.net/?comp=list"
200 = token/RBAC fine, points at network path. 403 = matches the app's failure, confirms1 = would actually indicate a real auth problem.

Sanity control (proves the private-link mechanism itself works when the zone is linked)

Resolve-DnsName cor-ais-cap-dev-we-001.cognitiveservices.azure.com
This one is confirmed correctly linked (260723) and should resolve straight to 10.243.4.10 with no fallthrough CNAME — useful as a side-by-side "this is what working looks like" next to the broken blob


# ExtractActivity 403 — recurrence, likely `corstdatacapdevwe` private DNS

## Symptom

Triggering `StartIndexing` fails inside the orchestration with:

```
Microsoft.Azure.Functions.Worker.Extensions.DurableTask.Exceptions.DurableSerializationException
System.InvalidOperationException: ExtractActivity failed: Service request failed.
Status: 403 (Forbidden)
```

Thrown from `PdfIndexingFunction.ExtractActivity` (`PdfIndexingFunction.cs:179`), same
call site as [260723/document-intelligence-403-dns-privatelink.md](../260723/document-intelligence-403-dns-privatelink.md).

## Relation to the 260723 writeup

The 260723 investigation found the same 403-from-`ExtractActivity` symptom and traced it
to private DNS zones linked to the *spoke* VNet (`cor-vnet-cap-dev-we-001`) but not to
the *hub* VNet that actually owns DNS resolution (custom resolver at `10.240.0.68`).
Confirmed broken at the time: `corstdatacapdevwe.blob.core.windows.net`,
`cor-srch-cap-dev-we-001.search.windows.net`, `corstfunccapdevwe.file.core.windows.net`,
and `*.openai.azure.com`. Confirmed working (private zone correctly linked to the hub):
`cor-ais-cap-dev-we-001.cognitiveservices.azure.com`.

Per the last check, `corstdatacapdevwe` (blob) was still resolving publicly and getting
rejected by `publicNetworkAccess: Disabled` — a 403 that happens before token validation,
so it presents identically to an auth problem (fast rejection, ~1-2ms elapsed-time,
no body) even though RBAC and the token are fine. This occurrence should be checked
against that same root cause first, since nothing in app code or RBAC changed between
then and now — only a platform-side DNS zone link would explain a repeat.

## What to check now

Confirm whether `privatelink.blob.core.windows.net` (and the other zones listed as
still-open in the 260723 doc: `privatelink.file.core.windows.net`,
`privatelink.openai.azure.com`) have since been linked to the hub VNet, and if not,
whether this is the same unresolved gap recurring, or a new regression (e.g. a zone
link that was in place got removed, or a different resource is now the one failing).

## Kudu commands to run

Open the Kudu console for the indexing function app:

`https://cor-func-idx-cap-dev-we-001.scm.azurewebsites.net/DebugConsole`

Run these from the Kudu **CMD/PowerShell** console (Bash isn't available there):

### 1. DNS resolution for every dependency the orchestration touches

Use `Resolve-DnsName` from the Kudu **PowerShell** console (not `nslookup` — platform
engineering prefers `Resolve-DnsName` since it goes through the OS's actual DNS client
resolution path instead of `nslookup`'s own resolver library, so it reflects what the
running app instance really sees):

```
Resolve-DnsName corstdatacapdevwe.blob.core.windows.net
Resolve-DnsName corstfunccapdevwe.file.core.windows.net
Resolve-DnsName cor-srch-cap-dev-we-001.search.windows.net
Resolve-DnsName cor-ais-cap-dev-we-001.cognitiveservices.azure.com
Resolve-DnsName cor-func-idx-cap-dev-we-001.azurewebsites.net

```

Expect all of these to resolve to a `10.243.4.x` address (the private endpoint / `pe`
subnet range). Anything resolving to a public cluster hostname/IP
(e.g. `*.store.core.windows.net`, `*.cloudapp.azure.com`) is the broken case — that
resource is the one whose call will 403.

#### Confirmed result (2026-07-27)

```
Resolve-DnsName corstdatacapdevwe.blob.core.windows.net
```

```
corstdatacapdevwe.blob.core.windows.net
  CNAME -> corstdatacapdevwe.privatelink.blob.core.windows.net
    CNAME -> blob.am5prdstrz28a.store.core.windows.net
      A -> 135.130.102.1
      A -> 57.150.225.193
      A -> 135.130.102.129
```

This is the same failure mode as 260723, reproduced against `corstdatacapdevwe` directly.
The privatelink CNAME hop (`corstdatacapdevwe.privatelink.blob.core.windows.net`) should
terminate in a private `10.243.4.x` A record if `privatelink.blob.core.windows.net` is
correctly linked to the hub VNet's resolver. Instead it falls through to the zone's
public CNAME (`blob.am5prdstrz28a.store.core.windows.net`) and resolves to public,
internet-routable IPs. This confirms the private DNS zone still is not answering from
the hub VNet context — a platform-side DNS zone-linking gap, not a function app
VNet-integration or networking-settings issue. (The `ping 8.8.8.8` test doesn't exercise
this path at all — ICMP to an arbitrary public IP has no relationship to CNAME/A
resolution of a specific private-link hostname.)

### 2. Confirm which DNS server the app is actually using

```
nslookup corstdatacapdevwe.blob.core.windows.net 10.240.0.68
ipconfig /all
```

The `ipconfig /all` output's "DNS Servers" line should show `10.240.0.68` (the hub
resolver) — if it shows something else, the DNS path assumption from 260723 no longer
applies and needs re-tracing.

### 3. Prove it's network-path, not RBAC/token, with a real call

Get a managed-identity token, then call the storage account's own REST endpoint with it
(a plain blob List/Get call is enough — no SDK needed):

```
curl -s -H "Metadata: true" "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2019-08-01&resource=https://storage.azure.com/" | more
```

Copy the `access_token` value from the response, then:

```
curl -s -i -H "Authorization: Bearer <token>" -H "x-ms-version: 2021-08-06" "https://corstdatacapdevwe.blob.core.windows.net/?comp=list"
```

- `200 OK` → RBAC and the token are fine; a prior 403 was the network path, matching
  260723's finding.
- `403` here too → network policy rejection, same as `ExtractActivity`'s error,
  confirming the DNS/private-link path is still the cause.
- `401` → would indicate an actual auth/RBAC regression, a different problem than 260723.

### 4. Route sanity check (optional, only if nslookup returns a private IP but the call still 403s)

```
tracert corstdatacapdevwe.blob.core.windows.net
```

Confirms whether traffic to the resolved private IP is actually leaving via the VNet
integration rather than some other egress path.

## Next step

If step 1 shows `corstdatacapdevwe.blob.core.windows.net` resolving publicly again,
this is the same unresolved gap from 260723 (`privatelink.blob.core.windows.net` not
linked to the hub VNet) — re-escalate to the platform team (`cor-connectivity-prd`)
rather than re-diagnosing from scratch. If it now resolves privately but the call still
403s, this is a new issue and the token/RBAC check in step 3 should be the next branch
point.
