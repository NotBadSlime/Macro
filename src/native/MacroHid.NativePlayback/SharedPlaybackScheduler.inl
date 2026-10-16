// Included inside NativePlayback.cpp's anonymous namespace.
// One next batch per run; no lock is held while waiting or submitting input.
inline bool BatchTraceEnabled()
{
    static const bool enabled = []{ wchar_t v[4]{}; return GetEnvironmentVariableW(L"MACROHID_BATCH_TRACE", v, 4) == 1 && v[0] == L'1'; }();
    return enabled;
}
// Typical SendInput call cost per batch kind, learned from real submissions. A key goes through every low-level keyboard
// hook in the session before SendInput returns; on a machine with input-method and vendor tools installed that measured
// 190-300 us median for a key versus 85-105 us for a mouse move. Submitting that much earlier makes the input land on
// the written time on average. Median of the last 64 samples (robust to the occasional multi-ms stall), clamped.
class SendCostTracker
{
public:
    enum Kind { Keyboard = 0, Mouse = 1, Count = 2 };
    static constexpr int Window = 64;
    static constexpr int64_t MaxLeadUs = 1000;
    void Note(int kind, int64_t costTicks)
    {
        auto& k = kinds[kind];
        std::lock_guard<std::mutex> lock(k.gate);
        k.samples[k.next] = costTicks; k.next = (k.next + 1) % Window; if (k.filled < Window) k.filled++;
        if (k.filled < 8) return;
        std::array<int64_t, Window> copy{};
        std::copy_n(k.samples.begin(), k.filled, copy.begin());
        std::nth_element(copy.begin(), copy.begin() + k.filled / 2, copy.begin() + k.filled);
        k.median.store(copy[k.filled / 2], std::memory_order_relaxed);
    }
    // Ticks to submit early; 0 until enough samples exist.
    int64_t Lead(int kind, int64_t frequency) const
    {
        const int64_t median = kinds[kind].median.load(std::memory_order_relaxed);
        return std::clamp<int64_t>(median, 0, frequency * MaxLeadUs / 1000000);
    }
    int64_t MedianUs(int kind, int64_t frequency) const
    {
        return kinds[kind].median.load(std::memory_order_relaxed) * 1000000 / std::max<int64_t>(1, frequency);
    }
private:
    struct PerKind { std::mutex gate; std::array<int64_t, Window> samples{}; int next = 0; int filled = 0; std::atomic<int64_t> median{0}; };
    PerKind kinds[Count];
};
inline SendCostTracker& SendCosts() { static SendCostTracker tracker; return tracker; }
inline int BatchKind(const INPUT* inputs, uint32_t count)
{
    for (uint32_t i = 0; i < count; i++) if (inputs[i].type == INPUT_MOUSE) return SendCostTracker::Mouse;
    return SendCostTracker::Keyboard;
}
// Manual-reset completion events are recycled: CreateEventW was a kernel call on every submit, inside the
// hotkey-to-first-input path. An event is reset when it comes back (off that path), after the run's last
// owner has released it, so no waiter can still be using it. The pool is bounded; extras are closed.
inline std::mutex& CompletionPoolGate() { static std::mutex gate; return gate; }
inline std::vector<HANDLE>& CompletionPool() { static std::vector<HANDLE> pool; return pool; }
inline HANDLE AcquireCompletionEvent()
{
    {
        std::lock_guard<std::mutex> lock(CompletionPoolGate());
        auto& pool = CompletionPool();
        if (!pool.empty()) { HANDLE event = pool.back(); pool.pop_back(); return event; }
    }
    return CreateEventW(nullptr, TRUE, FALSE, nullptr);
}
inline void ReturnCompletionEvent(HANDLE event)
{
    if (!event) return;
    if (ResetEvent(event))
    {
        std::lock_guard<std::mutex> lock(CompletionPoolGate());
        auto& pool = CompletionPool();
        if (pool.size() < 16) { pool.push_back(event); return; }
    }
    CloseHandle(event);
}
struct ScheduledRun
{
    NativePlan* plan;
    MhpRunOptions options;
    PlaybackControl* control;
    volatile long* externalCancel;
    std::atomic<bool> cancelled{false};
    HANDLE completed = nullptr;
    std::array<CoreChoice, 2> cores{};
    MhpRunStats stats{};
    MhpRunTrace trace{};
    std::vector<MhpBatchTrace> batchTrace; // reserved up front when tracing; empty otherwise
    MhpStatus status = MhpOk;
    int64_t start;
    int64_t frequency;
    size_t next = 0;
    int owner = 0;
    bool inFlight = false;
    bool done = false;
    ModifierChordClock chord;
    std::vector<int64_t> late;
    std::vector<int64_t> loopLate;
    std::vector<int64_t> submittedAt;
    int scratchSlot = -1;
    unsigned priorityWorkers = 0, mmcssWorkers = 0;
    ScheduledRun(NativePlan* p, const MhpRunOptions& o, PlaybackControl* c, volatile long* flag, int64_t origin)
        : plan(p), options(o), control(c), externalCancel(flag), start(0),
          frequency(o.qpcFrequency > 0 ? o.qpcFrequency : QueryFrequency())
    {
        completed = AcquireCompletionEvent();
        if (!completed) throw std::bad_alloc();
        try
        {
            std::lock_guard<std::mutex> lock(p->scratchGate);
            for (int i = 0; i < 2; i++) if (p->scratch[i].available)
            {
                scratchSlot = i; p->scratch[i].available = false;
                late.swap(p->scratch[i].late); loopLate.swap(p->scratch[i].loopLate); submittedAt.swap(p->scratch[i].submitted);
                break;
            }
            if (scratchSlot < 0) { late.reserve(p->batches.size()); loopLate.reserve(p->batches.size()); submittedAt.resize(p->batches.size()); }
            // Dependencies only refer to preceding batches, each overwritten
            // before it can be read in this run; do not clear the whole timeline
            // on a hot start.
            late.clear(); loopLate.clear();
        }
        catch (...) { ReturnCompletionEvent(completed); completed = nullptr; throw; }
        if (BatchTraceEnabled()) try { batchTrace.reserve(p->batches.size()); } catch (...) { ReturnCompletionEvent(completed); completed = nullptr; throw; }
        p->references.fetch_add(1);
        stats.selectedCpuSet = UINT32_MAX;
        stats.maxLateBatchIndex = UINT32_MAX;
        stats.maxLateCpu = UINT32_MAX;
        stats.maxLateWorker = -1;
        stats.standbyUsed = 1;
        start = origin;
    }
    ~ScheduledRun()
    {
        if (completed) ReturnCompletionEvent(completed);
        if (scratchSlot >= 0)
        {
            std::lock_guard<std::mutex> lock(plan->scratchGate);
            auto& slot = plan->scratch[scratchSlot];
            late.swap(slot.late); loopLate.swap(slot.loopLate); submittedAt.swap(slot.submitted);
            slot.available = true;
        }
        MhpDestroyPlan(plan);
    }
    bool IsStopped() const { return cancelled.load(std::memory_order_acquire) || IsCancelled(externalCancel); }
};

class SharedPlaybackScheduler
{
    std::mutex gate;
    std::mutex lifecycle;
    std::mutex eventGate;
    std::condition_variable initialized;
    int initializedWorkers = 0;
    std::vector<std::shared_ptr<ScheduledRun>> runs;
    std::array<std::thread, 2> workers;
    std::array<HANDLE, 2> changed{};
    std::array<CoreChoice, 2> cores{};
    std::atomic<uint64_t> generation{0};
    bool stopping = false;
    bool ready = false;
    bool measured = false;
    bool singleRunFastPath = true;
    // Precision the idle workers keep configured (MMCSS/affinity). Updated by every Warm() so a profile change
    // is applied while idle instead of in front of the next run's first SendInput.
    std::atomic<int> warmPrecision{1};
    DWORD_PTR preparedMask = 0;
    unsigned nextOwner = 0;

    void Finish(ScheduledRun& run, MhpStatus status)
    {
        run.status = status;
        run.done = true;
        run.trace.completed = QueryCounter();
        g_playbackActive.fetch_sub(1, std::memory_order_acq_rel);
        SetEvent(run.completed);
    }
    // Early submission for the expected SendInput cost of this batch (0 when the run did not ask for it).
    int64_t SendLead(ScheduledRun& run, size_t index)
    {
        if (!(run.options.flags & MhpFlagCompensateSendInput)) return 0;
        const auto& batch = run.plan->batches[index];
        if (!batch.inputCount) return 0;
        return SendCosts().Lead(BatchKind(run.plan->inputs.data() + batch.inputOffset, batch.inputCount), run.frequency);
    }
    int64_t Due(ScheduledRun& run)
    {
        const auto& batch = run.plan->batches[run.next];
        // The lead moves only the written time. Release dependencies and chord holds are applied after it, from the
        // measured start of the press, so a compensated hold is never shorter than written.
        int64_t due = run.start + batch.dueTicks + TotalPausedTicks(run.control) - SendLead(run, run.next);
        for (const auto& dependency : run.plan->releaseDependencies[run.next])
            due = std::max(due, run.submittedAt[dependency.first] + dependency.second);
        return run.chord.AdjustDue(due,
            run.plan->chordEdges[run.next], ModifierChordHoldTicks(run.frequency));
    }
    bool CanPumpSingleRun(ScheduledRun& run)
    {
        if (!singleRunFastPath) return false;
        if (run.options.precisionMode != MhpUltraLowJitter) return true;
        // Retain deadline rescue for spaced ultra events. A single owner was
        // cheaper but measurably worsened their tail latency; only dense bursts
        // avoid the per-batch handoff to the helper.
        const auto& batches = run.plan->batches;
        const int64_t denseTicks = std::max<int64_t>(1, run.frequency * 20 / 1000000);
        return run.next + 1 < batches.size()
            && batches[run.next + 1].dueTicks - batches[run.next].dueTicks <= denseTicks
            && Due(run) - QueryCounter() <= denseTicks;
    }
    // The caller owns inFlight. With exclusive ownership, other workers skip this
    // run and results remain private until its completion event is signalled.
    void SubmitClaimedBatch(ScheduledRun& run, int worker, bool priorityApplied, bool mmcss, bool exclusive, int64_t effectiveDue)
    {
        const size_t index = run.next;
        const auto& batch = run.plan->batches[index];
        const int64_t nominalDue = run.start + batch.dueTicks + TotalPausedTicks(run.control) - SendLead(run, index);
        const int64_t actual = QueryCounter();
        const DWORD cpu = CurrentProcessorNumber();
        UINT sent = 0;
        DWORD error = 0;
        if (!run.IsStopped())
        {
            sent = batch.inputCount ? SubmitPlaybackInput(batch.inputCount, run.plan->inputs.data() + batch.inputOffset) : 0;
            if (sent != batch.inputCount) error = GetLastError();
        }
        const int64_t submitted = QueryCounter();
        if (sent == batch.inputCount && batch.inputCount)
            SendCosts().Note(BatchKind(run.plan->inputs.data() + batch.inputOffset, batch.inputCount), submitted - actual);
        std::unique_lock<std::mutex> lock(gate, std::defer_lock);
        if (!exclusive) lock.lock();
        auto& stats = run.stats;
        if (priorityApplied && !(run.priorityWorkers & (1U << worker))) { run.priorityWorkers |= 1U << worker; stats.workerPriorityAppliedCount++; }
        if (mmcss && !(run.mmcssWorkers & (1U << worker))) { run.mmcssWorkers |= 1U << worker; stats.workerMmcssAppliedCount++; }
        const int64_t lateUs = std::max<int64_t>(0, ToMicroseconds(actual - nominalDue, run.frequency));
        if (!run.IsStopped() || sent > 0)
        {
            if (!run.trace.firstSubmit) { run.trace.firstSubmit = actual; run.trace.firstReturn = submitted; }
            run.trace.lastSubmit = actual;
            // The release dependency counts the hold from when this batch STARTED (actual), not from when its SendInput returned.
            // The hold the user wrote is the distance between the two planned times; if the press call itself takes a while
            // (measured 0.05 to 0.65 ms here), starting the clock at its return added that time to every hold and pushed every
            // release late by the same amount. actual is taken just before the call, so the hold is never shorter than written.
            // Only generation 0 (latest) counts from the start. Generations 1 (2.0.x), 2 (1.6.0) and 3 (2.0.2) keep the return time.
            run.submittedAt[index] = run.options.generation == 0 ? actual : submitted;
            run.late.push_back(lateUs);
            if (run.batchTrace.capacity() && run.batchTrace.size() < run.batchTrace.capacity())
                run.batchTrace.push_back({static_cast<uint32_t>(index), batch.inputCount, sent, cpu, worker, static_cast<int32_t>(error), nominalDue, effectiveDue, actual, submitted});
            stats.batchesSubmitted++;
            stats.nativeInputsSubmitted += sent;
            if (sent == batch.inputCount) stats.actionsSubmitted += batch.actionCount;
            stats.selectedCpuSet = cpu;
            stats.nativeSubmitDurationUs += ToMicroseconds(submitted - actual, run.frequency);
            if (index == 0) stats.engineWakeCostUs = ToMicroseconds(actual - run.start - batch.dueTicks, run.frequency);
            if (worker == run.owner) stats.primaryWorkerWins++; else stats.helperWorkerWins++;
            if (lateUs > stats.maxLateUs) { stats.maxLateUs = lateUs; stats.maxLateBatchIndex = static_cast<uint32_t>(index); stats.maxLateCpu = cpu; stats.maxLateWorker = worker; }
            const bool loopEnd = run.options.loopStepCount > 0 && (index + 1) % run.options.loopStepCount == 0;
            if (lateUs > run.options.outlierThresholdUs) { stats.outliersOverThreshold++; RecordOutlierEvent(stats, run.options, static_cast<uint32_t>(index), nominalDue, actual, lateUs, cpu, worker, loopEnd); }
            if (loopEnd) { run.loopLate.push_back(lateUs); stats.maxLoopEndLateUs = std::max(stats.maxLoopEndLateUs, lateUs); if (lateUs > run.options.outlierThresholdUs) stats.loopEndOutliersOverThreshold++; }
            if (sent == batch.inputCount && batch.inputCount) run.chord.NoteSent(run.plan->chordEdges[index], submitted);
        }
        run.next++;
        if (error || (!run.IsStopped() && sent != batch.inputCount))
        {
            stats.failedSubmissions++; stats.lastWin32Error = error; run.status = MhpWin32Error;
        }
        else if (run.IsStopped()) run.status = MhpCancelled;
        if (!exclusive)
        {
            run.inFlight = false;
            if (run.status != MhpOk || run.next == run.plan->batches.size()) Finish(run, run.status);
        }
    }
    void PumpSingleRun(ScheduledRun& run, int worker, HANDLE timer, bool priorityApplied, bool mmcss, uint64_t seen)
    {
        const int64_t baseSpinTicks = run.frequency * (run.options.precisionMode == MhpUltraLowJitter ? 1000 : 300) / 1000000;
        int64_t spinTicks = baseSpinTicks;
        const int64_t maximumSpinTicks = run.frequency * (run.options.precisionMode == MhpUltraLowJitter ? 1000 : 800) / 1000000;
        const int64_t timerMargin = run.frequency * 50 / 1000000;
        while (run.next < run.plan->batches.size() && run.status == MhpOk)
        {
            if (generation.load(std::memory_order_acquire) != seen || run.IsStopped() || IsPaused(run.control)) break;
            if (!CanPumpSingleRun(run)) break;
            const int64_t target = Due(run);
            const int64_t remaining = target - QueryCounter();
            // The first batch of a hotkey-anchored run is at most the start lead away: spin up to 1 ms instead of a timer
            // sleep, whose wake-up error (~100 us here) would break the constant start delay the anchor provides.
            const int64_t spinLimit = run.next == 0 ? std::max(spinTicks, run.frequency / 1000) : spinTicks;
            if (remaining > spinLimit + timerMargin)
            {
                const int64_t waitUs = std::min<int64_t>(10000, (remaining - spinTicks) * 1000000 / run.frequency);
                LARGE_INTEGER due{}; due.QuadPart = -std::max<int64_t>(1, waitUs * 10);
                const int64_t requestedWake = QueryCounter() + waitUs * run.frequency / 1000000;
                if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
                {
                    HANDLE handles[]{changed[worker], timer};
                    const DWORD result = WaitForMultipleObjects(2, handles, FALSE, 20);
                    if (result == WAIT_OBJECT_0 + 1)
                    {
                        const int64_t overshoot = std::max<int64_t>(0, QueryCounter() - requestedWake);
                        // Bounded per-run adaptation. External wakeups do not
                        // train it and a long OS stall cannot cause unlimited spin.
                        spinTicks = std::max(spinTicks, std::clamp(overshoot + timerMargin, baseSpinTicks, maximumSpinTicks));
                    }
                }
                else WaitForSingleObject(changed[worker], 1);
                continue;
            }
            while (QueryCounter() < target && generation.load(std::memory_order_acquire) == seen && !run.IsStopped()) PauseProcessor();
            if (generation.load(std::memory_order_acquire) != seen || run.IsStopped() || IsPaused(run.control)) break;
            SubmitClaimedBatch(run, worker, priorityApplied, mmcss, true, target);
        }
        {
            std::lock_guard<std::mutex> lock(gate);
            run.inFlight = false;
            if (run.status != MhpOk) Finish(run, run.status);
            else if (run.IsStopped() || stopping) Finish(run, MhpCancelled);
            else if (run.next == run.plan->batches.size()) Finish(run, MhpOk);
        }
        // Hand back ownership on completion or any external state change. No
        // per-batch queue scan, lock, helper claim, or SetEvent on the single lane.
        Notify();
    }
    void Loop(int worker)
    {
        HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        const bool priorityApplied = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST) != FALSE;
        HANDLE mmcss = nullptr;
        DWORD_PTR oldAffinity = 0;
        int lastMode = -1;
        DWORD_PTR lastMask = 0;
        auto configure = [&](int mode, DWORD_PTR mask)
        {
            if (lastMode != mode)
            {
                if (mmcss) { AvRevertMmThreadCharacteristics(mmcss); mmcss = nullptr; }
            }
            if (mode == MhpUltraLowJitter && !mmcss)
            {
                DWORD task = 0;
                mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
                if (mmcss) AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);
            }
            if (lastMode != mode || lastMask != mask)
            {
                if (oldAffinity) { SetThreadAffinityMask(GetCurrentThread(), oldAffinity); oldAffinity = 0; }
                if (mode == MhpUltraLowJitter && mask) oldAffinity = SetThreadAffinityMask(GetCurrentThread(), mask);
            }
            lastMode = mode; lastMask = mask;
        };
        // Prepared means timer, priority, MMCSS and affinity are ready before
        // the first deadline, not just that the std::thread has been created.
        configure(warmPrecision.load(std::memory_order_acquire), cores[worker].mask);
        { std::lock_guard<std::mutex> lock(gate); initializedWorkers++; }
        initialized.notify_one();
        while (true)
        {
            std::array<std::shared_ptr<ScheduledRun>, 16> retired;
            size_t retiredCount = 0;
            std::shared_ptr<ScheduledRun> picked;
            int64_t target = INT64_MAX;
            uint64_t seen;
            bool exclusive = false;
            {
                std::lock_guard<std::mutex> lock(gate);
                if (stopping && runs.empty()) break;
                for (auto& run : runs)
                {
                    if (run->done || run->inFlight) continue;
                    if (stopping || run->IsStopped()) { Finish(*run, MhpCancelled); continue; }
                    if (run->next == run->plan->batches.size()) { Finish(*run, MhpOk); continue; }
                    if (IsPaused(run->control)) continue;
                    if (runs.size() == 1 && CanPumpSingleRun(*run) && run->owner != worker) continue;
                    if (run->options.precisionMode != MhpUltraLowJitter && run->owner != worker) continue;
                    int64_t due = Due(*run);
                    if (run->owner != worker) due += std::max<int64_t>(1, run->frequency * 2 / 1000000);
                    if (due < target) { target = due; picked = run; }
                }
                for (auto it = runs.begin(); it != runs.end() && retiredCount < retired.size(); )
                {
                    if ((*it)->done) { retired[retiredCount++] = std::move(*it); it = runs.erase(it); }
                    else ++it;
                }
                if (stopping && runs.empty()) break;
                seen = generation.load(std::memory_order_acquire);
                exclusive = runs.size() == 1 && picked && CanPumpSingleRun(*picked) && picked->owner == worker;
                if (exclusive) picked->inFlight = true;
            }
            for (size_t i = 0; i < retiredCount; i++) retired[i].reset();
            if (picked && picked->IsStopped() && !exclusive) continue;
            if (!picked)
            {
                if (retiredCount) continue; // Return large buffers outside the queue lock.
                // Keep the prepared thread state while sleeping. Re-entering
                // MMCSS/affinity on every isolated hotkey is avoidable startup
                // work; mode changes and shutdown restore it below. A warm-up
                // for another profile wakes the workers so it is applied here.
                configure(warmPrecision.load(std::memory_order_acquire), cores[worker].mask);
                WaitForSingleObject(changed[worker], INFINITE);
                continue;
            }
            const auto mask = picked->cores[worker].mask;
            configure(picked->options.precisionMode, mask);
            if (exclusive)
            {
                PumpSingleRun(*picked, worker, timer, priorityApplied, mmcss != nullptr, seen);
                continue;
            }
            const int64_t spinUs = picked->next == 0 ? 1000 : picked->options.precisionMode == MhpUltraLowJitter ? 1000 : 300;
            int64_t remaining = target - QueryCounter();
            if (remaining > picked->frequency * (spinUs + 50) / 1000000)
            {
                const int64_t waitUs = std::min<int64_t>(10000, remaining * 1000000 / picked->frequency - spinUs);
                LARGE_INTEGER due{}; due.QuadPart = -std::max<int64_t>(1, waitUs * 10);
                if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
                {
                    HANDLE handles[]{changed[worker], timer};
                    WaitForMultipleObjects(2, handles, FALSE, 20);
                }
                else WaitForSingleObject(changed[worker], 1);
                continue;
            }
            while (QueryCounter() < target && generation.load(std::memory_order_acquire) == seen && !picked->IsStopped()) PauseProcessor();
            if (generation.load(std::memory_order_acquire) != seen) continue;
            int64_t due;
            {
                std::lock_guard<std::mutex> lock(gate);
                if (picked->done || picked->inFlight || picked->IsStopped() || IsPaused(picked->control)) continue;
                due = Due(*picked);
                const auto claimDue = due + (worker == picked->owner ? 0 : std::max<int64_t>(1, picked->frequency * 2 / 1000000));
                if (QueryCounter() < claimDue) continue;
                picked->inFlight = true;
            }
            SubmitClaimedBatch(*picked, worker, priorityApplied, mmcss != nullptr, false, due);
            Notify();
        }
        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
        if (oldAffinity) SetThreadAffinityMask(GetCurrentThread(), oldAffinity);
        if (timer) CloseHandle(timer);
    }
public:
    ~SharedPlaybackScheduler() { Shutdown(); }
    std::array<CoreChoice, 2> PreparedCores()
    {
        std::lock_guard<std::mutex> lock(gate);
        return measured ? cores : std::array<CoreChoice, 2>{};
    }
    void Notify()
    {
        generation.fetch_add(1, std::memory_order_release);
        std::lock_guard<std::mutex> lock(eventGate);
        for (auto event : changed) if (event) SetEvent(event);
    }
    void CopyCoreStats(MhpRunStats* stats)
    {
        if (!stats) return;
        stats->selectedWorker0 = cores[0].processorNumber;
        stats->selectedWorker1 = cores[1].processorNumber;
        stats->selectedWorker0MaxLateUs = measured ? cores[0].maxLateUs : INT64_MAX;
        stats->selectedWorker1MaxLateUs = measured ? cores[1].maxLateUs : INT64_MAX;
    }
    MhpStatus Warm(const MhpRunOptions* options, MhpRunStats* stats, DWORD_PTR allowedMask = 0)
    {
        std::lock_guard<std::mutex> life(lifecycle);
        if (stats) *stats = {};
        const int requestedPrecision = options ? options->precisionMode : 1;
        bool alreadyReady;
        bool precisionChanged = false;
        { std::lock_guard<std::mutex> lock(gate); alreadyReady = ready;
          if (ready) precisionChanged = warmPrecision.exchange(requestedPrecision, std::memory_order_acq_rel) != requestedPrecision; }
        if (precisionChanged) Notify();
        { std::lock_guard<std::mutex> lock(gate);
          if (ready && preparedMask == allowedMask && (!(options && options->enableCpuScan) || measured)) { CopyCoreStats(stats); return MhpOk; }
          if (ready && PlaybackIsActive()) { CopyCoreStats(stats); SetLastErrorText(L"core preparation deferred while playback is active"); return MhpWin32Error; } }
        try
        {
            const bool scan = options && options->enableCpuScan != 0;
            auto choices = SelectLowestJitterCores(2, scan, allowedMask);
            if (scan && (choices.size() < 2 || choices[0].physicalCoreId == choices[1].physicalCoreId))
            {
                SetLastErrorText(L"extreme profile requires two distinct physical cores");
                return MhpWin32Error;
            }
            { std::lock_guard<std::mutex> lock(gate);
              for (size_t i = 0; i < 2; i++) cores[i] = i < choices.size() ? choices[i] : CoreChoice{};
              measured = scan && !choices.empty(); preparedMask = allowedMask; CopyCoreStats(stats); }
            if (alreadyReady) return MhpOk;
            wchar_t disabled[8]{};
            singleRunFastPath = !(GetEnvironmentVariableW(L"MACROHID_DISABLE_SINGLE_RUN_FASTPATH", disabled, 8) > 0 && disabled[0] == L'1');
            warmPrecision.store(requestedPrecision, std::memory_order_release);
            std::unique_lock<std::mutex> events(eventGate);
            for (size_t i = 0; i < 2; i++)
            {
                changed[i] = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                if (!changed[i]) throw std::bad_alloc();
            }
            events.unlock();
            stopping = false;
            initializedWorkers = 0;
            for (int i = 0; i < 2; i++) workers[i] = std::thread([this, i] { Loop(i); });
            std::unique_lock<std::mutex> lock(gate);
            if (!initialized.wait_for(lock, std::chrono::seconds(3), [this] { return initializedWorkers == 2; }))
                throw std::runtime_error("worker warm-up timed out");
            ready = true;
            return MhpOk;
        }
        catch (...)
        {
            { std::lock_guard<std::mutex> lock(gate); stopping = true; }
            Notify();
            for (auto& worker : workers) if (worker.joinable()) worker.join();
            std::lock_guard<std::mutex> events(eventGate);
            for (auto& event : changed) { if (event) CloseHandle(event); event = nullptr; }
            return MhpWin32Error;
        }
    }
    std::shared_ptr<ScheduledRun> Submit(NativePlan* plan, const MhpRunOptions& options, PlaybackControl* control, volatile long* cancel, int64_t origin = 0)
    {
        const auto entry = QueryCounter();
        auto run = std::make_shared<ScheduledRun>(plan, options, control, cancel, origin > 0 ? origin : entry);
        run->trace.queued = entry;
        { std::lock_guard<std::mutex> lock(gate); if (!ready || stopping) return {}; run->owner = nextOwner++ % 2; run->cores = plan->configuredCores;
          run->stats.selectedWorker0 = run->cores[0].processorNumber; run->stats.selectedWorker1 = run->cores[1].processorNumber;
          run->stats.selectedWorker0MaxLateUs = run->cores[0].maxLateUs; run->stats.selectedWorker1MaxLateUs = run->cores[1].maxLateUs; runs.push_back(run); g_playbackActive.fetch_add(1, std::memory_order_acq_rel); }
        // Only 极限 lets the non-owner worker take a batch, so other profiles wake just the owner. The generation still
        // advances, so a worker spinning for another run still re-scans and sees this one.
        if (options.precisionMode == MhpUltraLowJitter) Notify();
        else NotifyWorker(run->owner);
        return run;
    }
    void NotifyWorker(int worker)
    {
        generation.fetch_add(1, std::memory_order_release);
        std::lock_guard<std::mutex> lock(eventGate);
        if (changed[worker]) SetEvent(changed[worker]);
    }
    MhpStatus Run(NativePlan* plan, const MhpRunOptions& options, PlaybackControl* control, volatile long* cancel, MhpRunStats& stats)
    {
        try
        {
            auto run = Submit(plan, options, control, cancel);
            if (!run) { SetLastErrorText(L"shared scheduler unavailable"); return MhpWin32Error; }
            WaitForSingleObject(run->completed, INFINITE);
            stats = run->stats;
            stats.p999LateUs = Percentile(run->late, .999);
            stats.p999LoopEndLateUs = Percentile(run->loopLate, .999);
            if (run->status == MhpWin32Error) SetLastWin32ErrorText(L"partial shared batch; do not replay", stats.lastWin32Error);
            return run->status;
        }
        catch (...) { SetLastErrorText(L"shared scheduler allocation failed before submission"); return MhpInvalidArgument; }
    }
    void Shutdown()
    {
        std::lock_guard<std::mutex> life(lifecycle);
        { std::lock_guard<std::mutex> lock(gate); ready = false; stopping = true; }
        Notify();
        for (auto& worker : workers) if (worker.joinable()) worker.join();
        std::lock_guard<std::mutex> events(eventGate);
        for (auto& event : changed) { if (event) CloseHandle(event); event = nullptr; }
    }
};
SharedPlaybackScheduler g_sharedScheduler;

bool LegacyPlaybackEnabled(const MhpRunOptions* options = nullptr)
{
    // Generation 2 (MacroHID 1.6.0) selects the older standby/inline engine per run. The environment variable stays as a
    // process-wide override for diagnostics.
    if (options != nullptr && options->generation == 2) return true;
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"MACROHID_LEGACY_PLAYBACK", value, 8) > 0 && value[0] == L'1';
}
