window.BENCHMARK_DATA = {
  "lastUpdate": 1772521847832,
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
      }
    ]
  }
}