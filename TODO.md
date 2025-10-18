# Project TODO

- [x] Step 1: Update Go diagnostics to expose new metrics hooks (wait group, select, channels)
- [x] Step 2: Add Go.DelayAsync with TimeProvider plumbing and new WaitGroup.WaitAsync overloads
- [x] Step 3: Introduce Go.SelectAsync helpers with ChannelCase support, instrumentation, and timeout/cancellation overloads
- [x] Step 4: Add prioritized channel factory overloads in Go
- [ ] Step 5: Optimize Error metadata storage using FrozenDictionary and adjust related helpers
- [x] Step 6: Introduce Go.PrioritizedChannel with priority-aware reader/writer
- [x] Step 7: Add tests for Go.PrioritizedChannel
- [ ] Step 8: Extend Result to handle IAsyncEnumerable pipelines (SequenceAsync/TraverseAsync/MapStreamAsync)
- [ ] Step 9: Augment unit tests (GoFunctionalTests, ResultTests, new streaming/select/time tests) leveraging FakeTimeProvider
- [ ] Step 10: Add necessary package references and update project files
- [ ] Step 11: Run test suite and iterate on any failures
