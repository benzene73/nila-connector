# Reproduce the GST sales fixtures

The SDK builds accounting-invoice XML and reads back native voucher fields.
`scripts/gst-fixtures.cs` owns fixture orchestration. Diagnostics is read-only.
This is a development fixture workflow, not a Nila product integration or a
return-filing tool. It supports one accounting sales line, zero cess, domestic
sales/notes, outward RCM, SEZ and exports with/without IGST. Special treatments
currently require sales invoices; special credit/debit notes are rejected.
It does not decide a transaction's GST treatment.

For SDK master creation and the verified RCM/SEZ/export batch, see
[gst-special-fixtures.md](gst-special-fixtures.md).

## Prerequisites and scope

Use .NET 10 (the repository `global.json`) and a loaded **disposable** Tally
company serving HTTP on `127.0.0.1:9000`. No new package or solution project is
needed: the harness is a .NET file-based app referencing Core.

The checked-in cases use CALTECH's synthetic fixture setup: seller GSTIN
`33BPBPK1081A1ZO`, state Tamil Nadu, registration name `Tamil Nadu Registration`.
The name and GUID must be supplied explicitly, and the harness verifies the
loaded company GSTIN against every case. A matching name alone is insufficient.
`--apply-disposable` is the operator's explicit declaration that this company
may receive test vouchers; disposability cannot be inferred from Tally.

The harness requires existing masters and never alters them. In a fresh
disposable company, configure these in Tally before running it:

- Manual numbering for Sales, Credit Note and Debit Note.
- Cash and the Output CGST, Output SGST and Output IGST tax ledgers.
- `Service Sales - 998719 @ 18%`: services, taxable, SAC 998719, GST 18%.
- `Nil Rated Sales - HSN 1006`: goods, nil rated, HSN 1006, GST 0%.
- `Exempt Education - SAC 9992`: services, exempt, SAC 9992, GST 0%.
- `Non-GST Sales - HSN 2710`: goods, non-GST, HSN 2710, GST 0%.
- The registered debtor ledgers named in the cases, with their corresponding
  GSTIN and state. `Table8 Registered Tamil Nadu` must use GSTIN
  `33AEWPH4158J1Z3`. Do not reuse an Andhra Pradesh debtor for this local case.
- Original sales NW-001, NW-002 and NW-004 dated 15-Jan-2026, respectively
  local B2CS, interstate B2CS and interstate B2CL. Their original Nila fixture
  values are 11,800, 90,000 and 110,000 including 18% GST.

Use the January fixture README in Nila for the complete original-sale setup.
This workflow recreates vouchers in a prepared company; it does not provision
a company or guarantee the legal classification of the synthetic products.

## Run

From the connector repository root:

```powershell
dotnet run --project src/NilaConnector.Diagnostics -- companies
dotnet run --project src/NilaConnector.Diagnostics -- vouchers --company 'CALTECH GST PRACTICE - DEC 2025' --from 2026-04-01 --to 2026-05-31

dotnet run --file scripts/gst-fixtures.cs -- --apply-disposable 'CALTECH GST PRACTICE - DEC 2025' fb7551b0-e015-4489-bb16-135a58fa492d scripts/fixtures/gst-sales.json artifacts/gst-fixtures

dotnet run --file scripts/gst-fixtures.cs -- --verify 'CALTECH GST PRACTICE - DEC 2025' fb7551b0-e015-4489-bb16-135a58fa492d scripts/fixtures/gst-sales.json artifacts/gst-fixtures
```

Cases contain six notes on 15-Apr-2026 and twelve Table 8 sales on 15-May-2026,
with `SDK-` numbers so the January–March Nila expectations remain untouched.
Use the same output directory for every rerun: it contains the attempt journal
and an exclusive run lock. Do not run competing harnesses with different output
directories against the same company. Tally does not provide an atomic
check-and-create operation through this workflow.

The harness preflights all cases and required ledger names, stops on duplicate
or different existing voucher identities, imports one voucher at a time, and
verifies amounts, party/POS, HSN, taxability, rates and original-note fields.
Exact existing fixtures are skipped; changed fixtures are **not** overwritten.
Successful repeat runs report `created 0; unchanged 18`. Readback alone does
not certify the native GST report; complete the export verification below.

Before each import the journal records an attempted write. A timeout, exception
or process crash must not lead to a blind retry. If the voucher exists and
matches, rerunning can reconcile it. If it is absent and a journal exists,
the harness stops. Check company GUID and exact period through diagnostics,
then independently inspect Day Book and **Alt+O → Exceptions** in Tally.
Archive the attempted journal only after resolving the outcome. An import
exception can exist even though Day Book contains no material voucher.

Requests, responses and readback JSON go under ignored `artifacts/`. They are
local audit output, not long-term Git fixtures. Keep PDFs and full extraction
audits in the private eval corpus. Nila owns model-extraction replay and the
native-return expectations; the connector does not generate those expectations.

## Native GSTR-1 export (UI)

1. Finish all writes. Reopen GSTR-1 with Alt+G. Confirm company/GSTIN and set
   **F2: Period** to exactly 1–30 April or 1–31 May 2026. Do not use an old
   report kept open while imports run: Tally can report that another user has
   modified the transactions during export.
2. April must include six vouchers: four B2CS adjustments and two CDNUR notes.
   Net values: taxable -3,000; IGST -360; CGST and SGST -90 each. May must include
   twelve vouchers, all Table 8, supply value 27,000 and no tax. Both reports
   must have zero uncertain transactions and no master conflicts.
3. Choose **Alt+E → GST Returns** (the screen is titled **Upload GST Returns**).
   Verify its period separately; its state may differ from the underlying
   report. Select the intended registration/period and choose **X: Export
   (Offline)**. Never use Send (Online), mark filed, or submit a return.
4. Choose JSON (Data Interchange), no separate section files, and an explicit
   output directory. Press **E: Export**. Verify the resulting JSON's `gstin`
   and `fp`; do not infer success merely from the export dialog closing.
5. Compare with Nila's February/March native returns: amounts/classification
   must agree. Account explicitly for April/May periods, `SDK-` document
   numbers, note dates, document-series boundaries and ordering. Preserve the
   native file unchanged; normalize only a separate comparison copy.

Save copies as `artifacts/gst-fixtures/native-2026-04.json` and
`native-2026-05.json`, then run:

```powershell
python scripts/compare-gst-fixtures.py artifacts/gst-fixtures ../nila
```

The comparison uses Python's standard library only. It checks every return
field after normalizing the explicit period/date/number changes, compares
document counts separately, and checks document endpoints against the cases.

For May, each group is nil/exempt/non-GST respectively:

| Native group | Nil | Exempt | Non-GST |
| --- | ---: | ---: | ---: |
| INTRAB2B | 1,100 | 2,100 | 3,100 |
| INTRAB2C | 1,300 | 2,300 | 3,300 |
| INTRB2B | 1,200 | 2,200 | 3,200 |
| INTRB2C | 1,400 | 2,400 | 3,400 |

## Native invoice PDF export (UI)

Open Day Book with Alt+G. F2 sets a single date; **Alt+F2** sets a range.
Select the exact voucher number, Enter to open it, then Ctrl+E for current
voucher export. Select PDF and an explicit output filename, then Export.
Exit voucher alteration without saving changes. Reconcile filenames and page
contents against voucher number/date and the diagnostic MasterID.

For a note, verify both the note date and the original invoice number/date in
the PDF. Original scratch scripts wrote the unrecognized `ORIGINVOICENO` tag:
Tally retained the original date but left the original number empty, and the
printed reference date fell back to the note date. The SDK now emits
`ORIGINVOICENUMBER`, `ORIGINVOICEDATE` and `REFERENCEDATE` and checks readback.
Printed behavior still requires verification for each Tally print layout.

## SDK details and export investigation

- `GstSalesVoucher.ToImportCommand` uses the existing validated import transport.
  Category is an explicit caller choice: `URDORIGINALSALEVALUE` is `B2C (Small)`
  or `B2C (Large)`, independent of the note amount.
- Debit/credit flags precede `AMOUNT`. Reversing that order produced a native
  balance exception despite mathematically balanced XML in the live experiment.
- Retain tax/HSN inference settings and all five rate heads, including zero
  cess. Tally's `&#4;` system values are significant: plain text does not have
  the same behavior. Validation strips only the four known values (`Not
  Applicable`, `Applicable`, `System Inferred`, `Unknown`) from its parsing
  copy; transport sends them intact.
- Native object inspection fetches full voucher fields by MasterID, then
  enforces returned ID and date. Collection-based inspection had omitted the
  original-sale and reference fields. Fingerprint reads retain their existing
  narrower collection request.

Official documentation supports report exports over HTTP and JSON/JSONEX
integration, but does not establish that a display report requested as JSON
is the same as the GST portal e-return. The fork has no verified native
GSTR-1 e-return/PDF export operation. These two exports therefore remain UI
steps; no speculative `ExportGstr1` API or hard-coded coordinate automation is
presented as supported. A future API needs a release-specific native report/
configuration mapping and equality checks against the UI artifacts first.

References: [Sample XML](https://help.tallysolutions.com/sample-xml/),
[JSON integration](https://help.tallysolutions.com/tally-prime-integration-using-json-1/),
[export action variables](https://help.tallysolutions.com/actions-events-buttons-keys-in-tdl/).

## Validation

`dotnet test tests/NilaConnector.Core.Tests` runs offline builder/parser tests.
Live validation on 5-Sep-2026 used the company GUID above. SDK fixture MasterIDs
are 47–64. Readback verified all eighteen and an apply rerun created zero.
The two native returns and the inspected PDF are archived in the private eval
corpus; `scripts/fixtures/live-verification.json` records their keys and hashes.
Both native returns matched Nila's prior expectations with only the explicit
period, number/date and document-order normalization described above. Native
SHA-256: April `9ecdf8c3bba65effe6960eb3c75a869a840e273e0eff2388da107fe2401ad423`;
May `c85accf6472594f783b0d749cf5c69cb93d14dffa5d9dbd9f59bf868f243a134`.
The SDK-NU-CN-B native PDF was rendered and inspected: it prints note date
15-Apr-26 and original invoice `NW-004 dt. 15-Jan-26`. No original-reference
date limitation remains for this tested classic print layout.
Native report verification is independent of the extraction model; no model
invocation belongs in connector tests.
