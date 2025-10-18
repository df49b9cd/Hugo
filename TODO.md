# Project TODO

1. Evaluate WaitGroup in Primitives.cs against baselines like Task.WhenAll, Parallel.ForEachAsync, and manual continuation chaining to understand scheduling overhead, under both low and high contention with cancellation-heavy workloads.

2. Compare RwMutex read/write paths to ReaderWriterLockSlim, SemaphoreSlim + lock, and AsyncReaderWriterLock under mixed read/write ratios to highlight throughput, starvation, and async fairness differences.

3. Exercise PrioritizedChannel<T> versus plain bounded/unbounded Channel<T> by measuring dequeue latency per priority level, backlog fairness, and the cost of priority switching with varied producer/consumer counts.

4. Measure Go.SelectAsync against direct Task.WhenAny loops and channel read polling to capture dispatch latency, timeout handling, and cancellation costs when many ChannelCase entries are registered.

5. Benchmark channel creation helpers (Go.MakeChannel, prioritized defaults, single-reader/single-writer toggles) to quantify allocation and steady-state throughput impacts of different option combinations.

6. Profile Pool<T> retrieval/return throughput versus ConcurrentBag<T> and Microsoft.Extensions.ObjectPool.DefaultObjectPool<T> under multithreaded load—track GC pressure, hit rates, and factory invocation counts.

7. Test Once.Do versus Lazy<T>, LazyInitializer.EnsureInitialized, and double-checked locking to document one-off initialization cost and contention behavior.

8. Analyze Result<T> functional combinators (e.g., Then, Map, Ensure, async variants) in pipeline-heavy scenarios compared to exception-driven control flow and language-integrated pattern matching; include allocations and branch prediction metrics.

9. Investigate locker quick paths: benchmark Mutex.EnterScope vs lock/Monitor.Enter for synchronous access, and add contention stress tests contrasting Mutex.LockAsync with SemaphoreSlim.WaitAsync.