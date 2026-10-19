// Sequential because every case owns a temporary storage root and several cases spawn independent resolver processes against it; a probe argument is handled before Expecto.
module Resolver.Tests.Main

open Expecto

let private suites =
    testSequenced (
        testList
            "resolver"
            [ Payload.tests
              Tree.tests
              Unix.tests
              Stat.tests
              Install.tests
              Launch.tests
              Storage.tests
              Transport.tests ])

[<EntryPoint>]
let main argv =
    match Resolver.Tests.Probe.run argv with
    | Some exitCode -> exitCode
    | None -> runTestsWithCLIArgs [] argv suites
