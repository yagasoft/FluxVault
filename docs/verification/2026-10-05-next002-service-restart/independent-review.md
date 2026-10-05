# Independent bounded review

Reviewer: the existing independent Astra service/security reviewer.

The design review approved a clean native service-process replacement against
the existing owned fixture. It required joined first-process work, distinct
process identities, ordinary product open without provisioning, A-only bounded
checkpoint validation, complete receipt/configuration comparisons, an
independently changed source hash, live PostgreSQL identity and fresh group
tokens. It excluded interrupted effects, reboot and full S07/G01 claims.

Concrete preflight found one readiness race: the final JSON name was visible
before writing and flushing completed. The correction writes a unique protected
sibling, flushes and closes it, then publishes with a non-replacing move;
cleanup targets only that temporary file. The reviewer inspected the correction
and approved preflight for fixture `0ee82f86c1404967b35dcb2910454df4` under the
existing owned-fixture authorisation. No new package/profile/trust or normal
installation authority was inferred.

The reviewer then accepted the actual run and complete teardown. The retained
evidence proves distinct native SYSTEM servers, complete configuration/receipt
persistence, replay without history changes, independently verified old/new
recovery hashes, persisted group access and subsequent revocation. Both
servers and 29 captured process identities are absent; all 56 journal resources
are retired. Normal installation snapshots remain unchanged. The 59 focused
tests and zero-warning Release build pass. No blocking runtime or teardown
finding remained. This acceptance excludes interrupted effects, reboot,
packaged identity, full S07/G01 and normal rollout.

The admitted-unknown follow-on was preflight-approved for fixture
`2b3ee620d8064a03a9537f36c1fb79af`, without a product hook or new resource class.
Its original unknown preview receipt persists across clean replacement. Removing
only A's fixed owned output makes a repeated effect observable; exact replay
must not recreate it. The reviewer inspected A8/A17, the exact unknown receipt
and no recreated output, successful teardown and the final zero-warning build,
then accepted this bounded extension. The two-fixture census proves all 58
captured identities absent and normal snapshots unchanged. This remains an
admitted unknown outcome across clean replacement, not a forced crash or an
interrupted effect.
