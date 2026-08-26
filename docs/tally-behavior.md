# Verified TallyPrime behavior

This document records behavior that Nila has observed directly from TallyPrime.
It is an engineering notebook, not a statement of Tally's public API contract.
Treat every entry as version-sensitive and preserve a regression test when the
behavior affects correctness.

## Test environment

- Tested: 24 August 2026
- Product: TallyPrime Gold on Windows
- Transport: HTTP/XML on `127.0.0.1:9000`
- Company used for write tests: `Nila` (playground data)
- Exact TallyPrime release/build: not captured

Never repeat write experiments against a customer's production company. Use a
disposable company and verify cleanup by reading the affected objects back.

## Confirmed observations

### A closed company can look like a successful empty response

An export request scoped with `SVCURRENTCOMPANY=Caltech` while Caltech was not
loaded returned `STATUS=1` and an empty collection. It did not return the often
quoted `Could not set SVCurrentCompany` error.

**Consequence:** do not interpret `STATUS=1` plus an empty deep-data collection
as proof that the company is loaded or genuinely contains no records. Discover
loaded companies first and require the target company to be present.

### Basic on-disk company discovery works without loading every company

An inline TDL `Company on Disk` collection returned company names and local
company numbers for companies in Tally's configured data path.

**Consequence:** this is useful for lightweight discovery, but it is not a
substitute for loading a company before reading ledgers, vouchers, GST details,
or other company-scoped accounting data. A local company number must not be
treated as a globally portable identity.

### A synchronous TDL HTTP request blocks the interactive UI

A deliberately delayed five-second outbound HTTP request left Tally unable to
respond to navigation or clicks until the response arrived.

**Consequence:** the connector owns all Internet communication. Do not use TDL
for cloud polling or other remote networking on Tally's interaction path.

### One import request may contain multiple vouchers

A single HTTP request containing three voucher messages was tested in this
order: valid, invalid because of a missing ledger, valid. Tally created both
valid vouchers and reported one exception for the invalid voucher.

**Consequence:** “one voucher per HTTP request” is not a Tally protocol
requirement. Multi-voucher imports can partially succeed.

### Batch import results do not provide dependable per-voucher attribution

The mixed import returned aggregate counters (`CREATED=2`, `EXCEPTIONS=1`) and
a line error. It did not return a complete result mapped to each submitted
voucher identity.

**Consequence:** Nila must keep voucher-level job identities and outcomes even
if transport is batched. For V1, one voucher per request remains the simplest
way to obtain precise success/failure attribution. If batching is introduced,
read-after-write reconciliation is required before retrying unresolved items.

### The tested master-import shape includes an inner `NAME`

Temporary ledgers were created successfully when both the `LEDGER` name
attribute and an inner `NAME` element were supplied.

**Consequence:** retain the known-working shape. This experiment did not prove
that every alternative shape fails, so do not describe the inner element as an
absolute protocol requirement without an isolated negative test.

### `REMOTEALTGUID` persists across Tally-assigned voucher identity

An earlier experiment that supplied `REMOTEID` and `GUID` produced
`CREATED=1` followed by `ALTERED=1`, but a subsequent export showed a
Tally-generated `REMOTEID`. That experiment did not establish a durable
caller-owned identity.

A later Payment/Receipt experiment omitted `VOUCHERNUMBER`, `GUID`, and
`REMOTEID`, and supplied a UUID-shaped `REMOTEALTGUID` child element instead.
Tally assigned its own voucher number, GUID, and MasterID while preserving the
supplied `REMOTEALTGUID`. The value remained queryable after creation and was
returned by TallyConnector's misleadingly named `Voucher.RemoteId` property,
which is serialized from the `REMOTEALTGUID` element.

**Consequence:** for Tally-numbered accounting vouchers, Nila uses a
deterministic `REMOTEALTGUID` as its correlation identity and leaves voucher
number, GUID, and MasterID to Tally. The connector preflights and reads back by
that value, then compares a semantic fingerprint before treating the voucher as
equivalent. `REFERENCE` remains a bank/business reference and is not used as
the accounting identity.

## Current design rules

1. Tally remains the accounting source of record.
2. Discover loaded companies before company-scoped operations.
3. Keep cloud networking out of Tally and inside Nila Connector.
4. Treat every voucher as an independent logical result, even inside a batch.
5. A positive Tally rejection is a failure; only a missing trustworthy response
   is unknown.
6. Never blindly retry an unknown voucher creation.
7. Preserve the raw Tally line error in diagnostics, but present a concise,
   actionable message to the user.
8. For Tally-numbered vouchers, correlate with `REMOTEALTGUID`, then verify the
   material voucher contents before accepting a retry or read-back.

## Open experiments

These are intentionally unresolved:

- Establish whether caller-supplied `REMOTEID` has any safe use distinct from
  `REMOTEALTGUID`; do not use it for Nila voucher correlation meanwhile.
- Capture the TallyPrime release/build in every future live-test fixture.
- Establish which useful GST identity fields, if any, are available from an
  unloaded `Company on Disk` object across supported TallyPrime versions.
- Convert stable findings into fake-server fixtures and narrowly scoped live
  smoke tests; do not make the normal unit test suite depend on a running Tally.
