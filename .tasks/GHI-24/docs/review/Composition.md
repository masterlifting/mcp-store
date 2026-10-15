# Publish pipeline composition seam

`ReleaseRunner.runPublishPipelineWithTransportFactory` contains the same
publication pipeline used by the CLI. Its first argument is a deferred
`unit -> ReleasePublish.HttpTransport` factory; the opt-in and token checks run
before that factory is invoked, before the download directory is created, and
before `ReleaseTransaction.run` begins. The private CLI wrapper supplies the
existing default HTTP transport factory.

This private seam exists so focused tests can call the actual pipeline with a
recording HTTP transport and verify requests against the canonical component
tag without a network connection. It adds no CLI option, environment override,
or externally supported publication mode.
