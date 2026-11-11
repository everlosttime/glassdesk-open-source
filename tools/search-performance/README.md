# SearchService comparison

Run from the repository root with the .NET 10 SDK, using a **copy** of a profile:

```powershell
& 'D:\dotnet10\dotnet.exe' run --project tools/search-performance/SearchPerformance.csproj -c Release -- '<copied-profile>' '<report.json>' '<label>'
```

The tool reads `apps.json` and `launcher-layout.json` without loading a writable
data store or launching any library application. Reports contain counts, timing,
allocations and result hashes; they do not list personal app names or paths.

It separately measures the copied library and a labeled 500-entry synthetic
library. Each dataset uses 14 fixed queries, 300 warmup queries and five batches
of 1,400 measured queries. Preserve a compiled baseline runner before changing
SearchService, then run baseline/candidate pairs in separate processes under the
same conditions. Compare the result hashes as well as timing and allocations.

The allocation total includes small measurement collections and result-signature
bookkeeping, identically in both variants. JIT, GC and system load can affect
timings, particularly cold calls and early batches. This measures search-service
work only; it does not measure whole-app CPU, retained memory, GPU frame rate,
capture compatibility or physical keyboard-to-screen latency. No forced GC or
working-set trimming is used.
