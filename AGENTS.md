# Nila Connector Agent Notes

## Tally diagnostics

- Never send improvised XML directly to a live Tally endpoint for inspection.
- Use the read-only `NilaConnector.Diagnostics` CLI.
- Every voucher read requires an explicit company and exact date or date range.
- Treat `NotFound` only as “not found in this verified company and period.”
- Never infer deletion from one read. Verify company GUID, period, and an
  independent Tally view before considering recovery.
- Do not add write, import, alter, cancel, delete, or raw-XML escape commands to
  the diagnostics project.
- Run write experiments only in a disposable company. Never automatically
  clean experimental vouchers from a non-disposable company.

## Verification

- Preserve the separation between generic Tally transport and Nila accounting
  decisions.
- Voucher acceptance is not accounting correctness. Verify read-back and the
  relevant GST/GSTR-1 report before treating an integration change as correct.
