# SEC1 Handoff inner-archive identity — production fix

`Handoff.fs` `readRawTransport` built `CandidateArchiveSnapshot` from `boundedBytes` (the outer transport ZIP): inner SHA / size / bytes were the outer's identity; embedded manifest came from the outer payload; the publisher would have uploaded the GitHub wrapper instead of the producer ZIP.

Fix: the verified `VerifiedEntry` whose `Name` matches `trustedPolicy.ArchiveName` provides inner-archive bytes; inner SHA / size are computed independently from those bytes; standalone manifest stays the outer `distribution.json` entry; embedded manifest is parsed from the INNER archive via `ReleaseCompare.observeBytes` (NOT the outer payload). `OuterDigest` / outer size are unchanged; all link / path / collision / length / identity guards, defensive-copy accessors, and read-only `MemoryStream(false)` semantics are preserved; no mutable array is exposed; constructor arguments + receipt comparison are fixed internally; `tests/Handoff.fs:176` is untouched.

`mayEdit=true` scoped to `Handoff.fs` + this doc, `mayBuild=true` once on `Release.fsproj`, `mayTest=false`, `mayDelegate=false`. No live platform / upload / `.env` / secret / credential / network / state / commit / push / foreign-worktree change performed.
