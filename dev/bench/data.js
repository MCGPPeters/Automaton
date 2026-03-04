window.BENCHMARK_DATA = {
  "lastUpdate": 1772622955783,
  "repoUrl": "https://github.com/MCGPPeters/Automaton",
  "entries": {
    "Automaton Benchmarks": [
      {
        "commit": {
          "author": {
            "email": "me@mauricepeters.dev",
            "name": "MCGPPeters",
            "username": "MCGPPeters"
          },
          "committer": {
            "email": "MCGPPeters@users.noreply.github.com",
            "name": "Maurice CGP Peters",
            "username": "MCGPPeters"
          },
          "distinct": true,
          "id": "d9bb40649735c9fd084ec25e0cd59ac9a4fe9dcc",
          "message": "fix(ci): add Automaton.Patterns to NuGet publish and fix benchmark shallow clone",
          "timestamp": "2026-03-03T08:10:07+01:00",
          "tree_id": "ec63cc5b70cd93a799835d339b508e581ab5d5fb",
          "url": "https://github.com/MCGPPeters/Automaton/commit/d9bb40649735c9fd084ec25e0cd59ac9a4fe9dcc"
        },
        "date": 1772521847472,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Single",
            "value": 5948.409090909091,
            "unit": "ns",
            "range": "± 231.6843600809754"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithObserver",
            "value": 6121.75,
            "unit": "ns",
            "range": "± 97.73631036434905"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Batch_100",
            "value": 52883.32323232323,
            "unit": "ns",
            "range": "± 6632.172580636961"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithFeedback",
            "value": 7609.272727272727,
            "unit": "ns",
            "range": "± 105.12223353165581"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_ComposedObserver",
            "value": 6105.4,
            "unit": "ns",
            "range": "± 145.64065366510823"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Accept",
            "value": 6527.846153846154,
            "unit": "ns",
            "range": "± 101.38856457037464"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Reject",
            "value": 4068.6717171717173,
            "unit": "ns",
            "range": "± 499.4832644211586"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_Single",
            "value": 5757.5,
            "unit": "ns",
            "range": "± 201.09215687230457"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_WithFeedback",
            "value": 6847.846153846154,
            "unit": "ns",
            "range": "± 81.53817730961589"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Accept",
            "value": 6713.961538461538,
            "unit": "ns",
            "range": "± 110.72986301853066"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Reject",
            "value": 3822.769230769231,
            "unit": "ns",
            "range": "± 61.25922222565602"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_Single",
            "value": 4343.153846153846,
            "unit": "ns",
            "range": "± 57.60620069900773"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_WithFeedback",
            "value": 6146.538461538462,
            "unit": "ns",
            "range": "± 70.56629906763258"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Accept",
            "value": 6113.5,
            "unit": "ns",
            "range": "± 342.1092487718479"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Reject",
            "value": 3267.269230769231,
            "unit": "ns",
            "range": "± 56.70266578999886"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "me@mauricepeters.dev",
            "name": "MCGPPeters",
            "username": "MCGPPeters"
          },
          "committer": {
            "email": "MCGPPeters@users.noreply.github.com",
            "name": "Maurice CGP Peters",
            "username": "MCGPPeters"
          },
          "distinct": true,
          "id": "10e50fb402565a0bc9d60718596d610f7e7e417f",
          "message": "fix: resolve Copilot review comments on PR #12\n\n- Replace unused 'effect' variables with discards in RetryAutomatonTests\n- Add MaxAttempts validation (ArgumentOutOfRangeException) in Retry\n- Use FailureReason.Unknown for non-retryable exceptions instead of RetriesExhausted\n- Catch OperationCanceledException during backoff delay, return Cancelled result\n- Make CircuitBreaker BreakDuration configurable via Closed state (remove hardcoded 30s)\n- Catch OperationCanceledException on CircuitBreaker gate wait\n- Catch OperationCanceledException on RateLimiter gate wait\n- Observe remaining tasks in Hedging to prevent UnobservedTaskException\n- Add attempt validation in Backoff.Compute\n- Update Pipeline docs to reflect Init-based startup\n- Make Pending state carry Strategy/Operation for correct Execute dispatch\n- Update WithRetry docs to reflect actual default ShouldRetry behavior\n- Add UnwrapPipelineError helper in RetryInterpreterExtensions\n- Update tests for new Closed(BreakDuration) parameter and cancellation behavior",
          "timestamp": "2026-03-04T08:21:21+01:00",
          "tree_id": "85368fd0266d1a49ba626ec6832cd5f7327a2922",
          "url": "https://github.com/MCGPPeters/Automaton/commit/10e50fb402565a0bc9d60718596d610f7e7e417f"
        },
        "date": 1772608922105,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Single",
            "value": 5834.111111111111,
            "unit": "ns",
            "range": "± 146.11937515220833"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithObserver",
            "value": 6156.931818181818,
            "unit": "ns",
            "range": "± 403.2434818352719"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Batch_100",
            "value": 34079.07142857143,
            "unit": "ns",
            "range": "± 399.5598471175143"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithFeedback",
            "value": 8052.9,
            "unit": "ns",
            "range": "± 351.5791676295841"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_ComposedObserver",
            "value": 6176.5625,
            "unit": "ns",
            "range": "± 197.52499841792178"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Accept",
            "value": 6774.780821917808,
            "unit": "ns",
            "range": "± 338.33950103472426"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Reject",
            "value": 3772.8133333333335,
            "unit": "ns",
            "range": "± 200.3214102071653"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_Single",
            "value": 4853.579545454545,
            "unit": "ns",
            "range": "± 279.7760895188467"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_WithFeedback",
            "value": 6695.617647058823,
            "unit": "ns",
            "range": "± 219.1942565474443"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Accept",
            "value": 5937.673469387755,
            "unit": "ns",
            "range": "± 244.0429767270427"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Reject",
            "value": 3697.6475409836066,
            "unit": "ns",
            "range": "± 177.33948949830415"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_Single",
            "value": 4325.576271186441,
            "unit": "ns",
            "range": "± 199.1947758792393"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_WithFeedback",
            "value": 6238.895833333333,
            "unit": "ns",
            "range": "± 252.12313266501883"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Accept",
            "value": 5545.757575757576,
            "unit": "ns",
            "range": "± 268.67641736746685"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Reject",
            "value": 3206.16,
            "unit": "ns",
            "range": "± 138.34493419105206"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "me@mauricepeters.dev",
            "name": "MCGPPeters",
            "username": "MCGPPeters"
          },
          "committer": {
            "email": "MCGPPeters@users.noreply.github.com",
            "name": "Maurice CGP Peters",
            "username": "MCGPPeters"
          },
          "distinct": true,
          "id": "e164d8cc498aad53251d0743cc14d8aa1502f215",
          "message": "fix: resolve Copilot review comments on PR #13\n\n- Fix grammar: 'initial effects is' → 'initial effect is' (automaton.md, SagaRunner.cs)\n- Fix typo: 'web stie' → 'website' (conduit.instructions.md)\n- Fix typo: 'could effect' → 'could affect' (pr.instructions.md)\n- Rename test methods: Init_ → Initialize_ (5 resilience test files)",
          "timestamp": "2026-03-04T12:15:11+01:00",
          "tree_id": "9b5288536f5dad0b43b28d8efd8647ace39bd7f0",
          "url": "https://github.com/MCGPPeters/Automaton/commit/e164d8cc498aad53251d0743cc14d8aa1502f215"
        },
        "date": 1772622955310,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Single",
            "value": 6210.2307692307695,
            "unit": "ns",
            "range": "± 174.41847555630287"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithObserver",
            "value": 6167.340909090909,
            "unit": "ns",
            "range": "± 237.3145242158716"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_Batch_100",
            "value": 38458.5,
            "unit": "ns",
            "range": "± 668.5585815192838"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_WithFeedback",
            "value": 9232.75,
            "unit": "ns",
            "range": "± 245.9298680518493"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Dispatch_ComposedObserver",
            "value": 7168.784946236559,
            "unit": "ns",
            "range": "± 504.08090813363884"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Accept",
            "value": 6630.777777777777,
            "unit": "ns",
            "range": "± 143.59237877349227"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Handle_Reject",
            "value": 4662.789855072464,
            "unit": "ns",
            "range": "± 237.04331333232523"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_Single",
            "value": 4744.474358974359,
            "unit": "ns",
            "range": "± 175.13776339596618"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Dispatch_WithFeedback",
            "value": 6856.428571428572,
            "unit": "ns",
            "range": "± 77.02693263819059"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Accept",
            "value": 6618.40625,
            "unit": "ns",
            "range": "± 717.9936970567067"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Safe_NoTrack_Handle_Reject",
            "value": 3817.189189189189,
            "unit": "ns",
            "range": "± 133.92697799709896"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_Single",
            "value": 4347.868421052632,
            "unit": "ns",
            "range": "± 185.79247558758237"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Dispatch_WithFeedback",
            "value": 7280.833333333333,
            "unit": "ns",
            "range": "± 345.47641947064403"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Accept",
            "value": 5796.938144329897,
            "unit": "ns",
            "range": "± 560.1265819443739"
          },
          {
            "name": "Automaton.Benchmarks.AutomatonBenchmarks.Lean_Handle_Reject",
            "value": 3401.4848484848485,
            "unit": "ns",
            "range": "± 387.97294676452674"
          }
        ]
      }
    ]
  }
}