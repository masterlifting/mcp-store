# Bootstrap standalone test evidence

- Prior baseline: 49/50 scenarios passed; this is retained as pre-fix evidence, not a passing verification.
- Diagnosis: `receipt-candidate-overlap` expects the rejected candidate root to remain absent, while the old failure assertion also required a receipt written under that forbidden candidate path.
- Fixture change: the overlap scenario now explicitly expects no receipt file and requires the `receipt-overlaps-candidate` typed child diagnostic; `ExpectCandidateRootAbsent = $true` remains unchanged. Failures with safe receipt destinations still require the typed receipt failure code.
- Verification: suite rerun deferred until the parallel production guard fix is complete; rerun the 50-scenario suite afterward.
