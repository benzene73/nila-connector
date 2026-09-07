# Outward RCM, SEZ and exports: verified fixture workflow

Use the company/GUID checks and offline export steps in [gst-fixtures.md](gst-fixtures.md).
The shared desktop needs coordination before native PDF/report export. No online
submission, automatic cleanup, company creation, or production master edits.

`scripts/fixtures/gst-special-sales.json` defines six service invoices: local and
interstate RCM, SEZ with IGST and LUT/bond, and exports with IGST and LUT/bond.
They use uniquely named `SDK3-*` customer/sales masters. Sales numbering must
be manual; Output IGST must already exist and be configured as an IGST ledger.
The synthetic service classifications exercise software, not legal eligibility.

In an empty disposable period, provision the named ledgers through the SDK:

```powershell
dotnet run scripts/gst-masters.cs -- --apply-disposable "COMPANY" "GUID" scripts/fixtures/gst-special-sales.json artifacts/special-masters
dotnet run scripts/gst-fixtures.cs -- --apply-disposable "COMPANY" "GUID" scripts/fixtures/gst-special-sales.json artifacts/special-vouchers
```

Existing masters are never altered. A pre-write journal records each master
plan; an existing name without the matching journal aborts. An unresolved prior
attempt also aborts instead of blindly retrying. Inspect the final readback and
native GSTR-1 before declaring correctness. The harness verifies ledger names,
not every pre-existing ledger's GST configuration.

## Tally-specific requirements

- `CreateLedgerCommand.SalesGst` configures dated HSN/SAC, service/goods, GST rate
  and treatment through typed SDK masters. SEZ parties use `Regular - SEZ`.
- GST applicable, state `Any`, and cess valuation use native U+0004 system
  names. The vendored SDK writer emits decimal `&#4;`, not hexadecimal
  `&#x4;`; Tally rejected the latter. Changes are in the vendored TallyConnector
  source as well as Core and must travel together when publishing.
- The generated state-wise read line must retain a visible field. Making every
  field invisible caused native Tally to abort during GST master reads.
- RCM needs ledger `GSTOVRDNISREVCHARGEAPPL = &#4; Applicable` and
  `GSTOVRDNSTOREDNATURE = &#4; System Inferred` as well as the header flag.
  Do not impose a plain-text local/interstate nature override. The party total
  excludes tax and no output-tax ledger entry is created, but GSTR-1 reports
  the recipient's tax with `rchrg: Y`.
- Exports need the foreign country, no recipient GSTIN and native registration
  `&#4; Unknown`. `Unregistered/Consumer` gave a master conflict even when the
  invoice appeared in the return.
- Paid SEZ/export sales use IGST even for a same-state SEZ. LUT/bond sales have
  zero filed tax and no output-tax posting. Their native PDFs can still print
  hypothetical HSN-summary tax; invoice payable remains exclusive of it.

## Independent native proof, 6 September 2026

Company `CALTECH GST PRACTICE - DEC 2025`, GUID
`fb7551b0-e015-4489-bb16-135a58fa492d`, GSTIN `33BPBPK1081A1ZO`.
June 2026 invoices `SDK3-*` (MasterIDs 72–77) supplied the six native PDFs for
Nila's production-Qwen replay. June setup involved iterative repairs, so a
fresh SDK-only reproduction was performed separately.

Twelve `SDK3-FINAL-*` buyer/sales masters were created using the final SDK.
Six invoices dated 15-Apr-2027 (`SDK3-XP-*`) then produced a native return with
six included invoices, zero uncertain transactions and zero master conflicts.
No manual invoice repairs were needed in this final batch. Intermediate July
2026–March 2027 experiments remain untouched in the disposable company.

Both native returns have taxable value 210,000, invoice value 224,400, reported
GST 19,800 (including recipient RCM tax 5,400). SEZ taxable value is 70,000
with IGST 5,400; exports are 110,000 with IGST 9,000. Native HSN B2B/B2C financial
values match between batches. Document-series endpoints differ with numbering
and creation order. `scripts/fixtures/special-live-verification.json` records
the private archive hash and exact final cases.

Nila's June fixture replays six actual Qwen extractions with one flagged RCM
correction. Invoice/HSN financial values match native Tally; HSN descriptions
and Table 13 inferred series remain visible differences. See the neighboring
`test/fixtures/gstr1/tally/2026-06/README.md`. Verified scope is INR service
invoices, not goods-export shipping details or currency conversion.

Run `dotnet test tests/NilaConnector.Core.Tests` for offline regression checks.
