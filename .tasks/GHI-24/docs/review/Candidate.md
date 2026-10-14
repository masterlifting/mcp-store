# Exact-three candidate transport

The compiled candidate handoff now requires exactly the canonical producer ZIP, `distribution.json`, and `preparation.json`. The preparation sidecar is parsed from verified outer transport bytes and its four hashes/sizes are checked against the inner archive and standalone manifest before snapshot creation. It remains untrusted consistency data, not policy or publisher provenance.

`CandidateArchiveSnapshot.Preparation` supplies the parsed receipt. The publication pipeline's private transport-factory helper now has eight arguments; the separate preparation argument and `--preparation-receipt` CLI flag/loaders are removed. Old exact-two transports and obsolete flags are not supported.

Release build `BD3b1FdIDQJJMIvbihRZbkinbfnx1ohKvBxDKrNKobk` reported zero errors and warnings. Test migration, execution, workflow wiring and independent reviews remain pending. No publication or readiness approval is claimed.
