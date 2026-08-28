# KaguyaHQ TallyConnector fork

Nila Connector builds against the public
[`KaguyaHQ/TallyConnector`](https://github.com/KaguyaHQ/TallyConnector) fork,
tracked as the `vendor/TallyConnector` Git submodule. The gitlink pins the exact
source revision used by local builds, CI, and releases; Nila does not wait for an
upstream NuGet publication.

The fork follows
[`Accounting-Companion/TallyConnector`](https://github.com/Accounting-Companion/TallyConnector)
and should contain only small, generally useful compatibility fixes. Nila's
accounting decisions and company-specific behavior remain in this repository.

## Current divergence

- `b2a2103`: expose the ledger `GSTDUTYHEAD` field as the typed
  `Ledger.GSTDutyHead` property. The same change is proposed upstream in
  [PR #53](https://github.com/Accounting-Companion/TallyConnector/pull/53).

## Updating the pinned dependency

1. Merge or cherry-pick the required change into `KaguyaHQ/TallyConnector`.
2. In `vendor/TallyConnector`, check out and test the intended commit.
3. Commit the resulting submodule gitlink update in Nila Connector.
4. Run `dotnet test` before releasing the connector.

Do not point builds at a floating branch and do not restore the upstream NuGet
package alongside the project reference. One exact source revision must own the
TallyConnector types used by a build.
