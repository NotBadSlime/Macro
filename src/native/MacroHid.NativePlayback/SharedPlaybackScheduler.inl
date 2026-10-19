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
// Logical processors the stall probe found quiet (0 = unknown / not restricted). Used as the soft affinity of the shared
// workers outside 极限. Written by MhpScanCoreStalls / MhpSetQuietCores, read by idle and running workers.
inline std::atomic<DWORD_PTR> g_quietCoreMask{0};
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
    // Max lock-free race: published = batches fully submitted (release after the winner finishes one), claimed = batches
    // taken. A worker claims batch i by moving claimed from i to i + 1; only then does it submit.
    std::atomic<size_t> published{0};
    std::atomic<size_t> claimed{0};
    int racers = 0; // under the scheduler gate
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
    // Max (2.1): warm profile is 极限 with preemption; hot standby = idle workers keep spinning while a Max hotkey listens.
    std::atomic<bool> warmMax{false};
    std::atomic<bool> hotStandby{false};
    DWORD previousPriorityClass = 0;
    bool realtimeApplied = false;
    DWORD_PTR preparedMask = 0;
    unsigned nextOwner = 0;
    static bool IsMax(const ScheduledRun& run) { return (run.options.flags & MhpFlagMaxPreemption) != 0; }
    // Max power guard. On 2026-10-19 this machine bug-checked 0x9F (amdacpbus.sys never finished a power IRP) 67 s after
    // leaving Modern Standby while priority-31 threads spun on its cores. While Windows suspends, enters standby or the
    // display is off/dimmed, and for a settle period after it returns, Max behaves like 极限: no spinning through waits, no
    // dual race, no hot standby, no SMT parkers, HIGHEST instead of TIME_CRITICAL, and the process priority class restored.
    std::atomic<bool> powerHeld{false};
    std::mutex powerGate;
    bool powerSuspended = false, powerDisplayOff = false, powerRegistered = false;
    HPOWERNOTIFY suspendNotify = nullptr, displayNotify = nullptr;
    PTP_TIMER powerSettleTimer = nullptr;
    DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS powerSubscription{};
    int64_t powerSettleMs = 120000;
    bool MaxLive(const ScheduledRun& run) const { return IsMax(run) && !PowerHeld(); }
    static ULONG CALLBACK OnPowerNotification(PVOID context, ULONG type, PVOID setting)
    {
        auto self = static_cast<SharedPlaybackScheduler*>(context);
        if (type == PBT_APMSUSPEND) self->OnPowerSuspend(true);
        else if (type == PBT_APMRESUMEAUTOMATIC || type == PBT_APMRESUMESUSPEND) self->OnPowerSuspend(false);
        else if (type == PBT_POWERSETTINGCHANGE && setting)
        {
            // GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed. Dimmed already means idle; hold from there.
            const auto broadcast = static_cast<const POWERBROADCAST_SETTING*>(setting);
            if (broadcast->DataLength >= sizeof(DWORD))
                self->OnPowerDisplay(*reinterpret_cast<const DWORD*>(broadcast->Data) != 1);
        }
        return ERROR_SUCCESS;
    }
    static VOID CALLBACK OnPowerSettled(PTP_CALLBACK_INSTANCE, PVOID context, PTP_TIMER)
    {
        auto self = static_cast<SharedPlaybackScheduler*>(context);
        {
            std::lock_guard<std::mutex> lock(self->powerGate);
            if (self->powerSuspended || self->powerDisplayOff) return;
            self->powerHeld.store(false, std::memory_order_release);
        }
        self->ApplyPowerState();
    }
    void RegisterPower()
    {
        {
            std::lock_guard<std::mutex> lock(powerGate);
            if (powerRegistered) return;
            powerRegistered = true;
            powerSettleTimer = CreateThreadpoolTimer(OnPowerSettled, this, nullptr);
            powerSubscription.Callback = OnPowerNotification;
            powerSubscription.Context = this;
        }
        // Outside powerGate: the display registration may report the current state on this thread.
        HPOWERNOTIFY suspend = nullptr, display = nullptr;
        PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, &powerSubscription, &suspend);
        // {6FE69556-704A-47A0-8F24-C28D936FDA47} GUID_CONSOLE_DISPLAY_STATE. Reports the current state at once.
        static const GUID consoleDisplayState{0x6fe69556, 0x704a, 0x47a0, {0x8f, 0x24, 0xc2, 0x8d, 0x93, 0x6f, 0xda, 0x47}};
        PowerSettingRegisterNotification(&consoleDisplayState, DEVICE_NOTIFY_CALLBACK, &powerSubscription, &display);
        std::lock_guard<std::mutex> lock(powerGate);
        suspendNotify = suspend; displayNotify = display;
    }
    void UnregisterPower()
    {
        HPOWERNOTIFY suspend, display; PTP_TIMER timer;
        { std::lock_guard<std::mutex> lock(powerGate);
          if (!powerRegistered) return;
          suspend = suspendNotify; display = displayNotify; timer = powerSettleTimer;
          suspendNotify = displayNotify = nullptr; }
        if (suspend) PowerUnregisterSuspendResumeNotification(suspend);
        if (display) PowerSettingUnregisterNotification(display);
        { std::lock_guard<std::mutex> lock(powerGate);
          powerRegistered = false; powerSettleTimer = nullptr; powerSuspended = powerDisplayOff = false;
          powerHeld.store(false, std::memory_order_release); }
        if (timer) { SetThreadpoolTimer(timer, nullptr, 0, 0); WaitForThreadpoolTimerCallbacks(timer, TRUE); CloseThreadpoolTimer(timer); }
    }
    void UpdatePowerHold()
    {
        {
            std::lock_guard<std::mutex> lock(powerGate);
            if (powerSuspended || powerDisplayOff)
            {
                if (powerSettleTimer) SetThreadpoolTimer(powerSettleTimer, nullptr, 0, 0);
                powerHeld.store(true, std::memory_order_release);
            }
            else if (PowerHeld())
            {
                // Back from standby / display on: drivers are still finishing their power transitions. Stay held a while.
                if (powerSettleMs <= 0 || !powerSettleTimer) powerHeld.store(false, std::memory_order_release);
                else
                {
                    ULARGE_INTEGER relative{}; relative.QuadPart = static_cast<ULONGLONG>(-powerSettleMs * 10000);
                    FILETIME due{relative.LowPart, relative.HighPart};
                    SetThreadpoolTimer(powerSettleTimer, &due, 0, 100);
                }
            }
        }
        ApplyPowerState();
    }
    void ApplyPowerState()
    {
        { std::lock_guard<std::mutex> lock(gate); ApplyProcessClass(warmMax.load(std::memory_order_acquire) && !PowerHeld()); }
        Notify(); // spinning workers and parkers re-check; idle workers re-configure their priority
    }
public:
    bool PowerHeld() const { return powerHeld.load(std::memory_order_acquire); }
    void OnPowerSuspend(bool suspended) { { std::lock_guard<std::mutex> lock(powerGate); powerSuspended = suspended; } UpdatePowerHold(); }
    void OnPowerDisplay(bool off) { { std::lock_guard<std::mutex> lock(powerGate); powerDisplayOff = off; } UpdatePowerHold(); }
    void SetPowerSettleMs(int64_t ms) { std::lock_guard<std::mutex> lock(powerGate); powerSettleMs = ms; }
private:
    // Diagnostics for the Max A/B: MACROHID_MAX_NOSPIN=1 keeps the timer sleeps, MACROHID_MAX_NORT=1 keeps the process class.
    static bool MaxEnv(const wchar_t* name) { wchar_t v[4]{}; return GetEnvironmentVariableW(name, v, 4) == 1 && v[0] == L'1'; }
    static bool MaxPureSpin() { static const bool on = !MaxEnv(L"MACROHID_MAX_NOSPIN"); return on; }
    static bool MaxRealtime() { static const bool on = !MaxEnv(L"MACROHID_MAX_NORT"); return on; }
    bool SpinsThrough(const ScheduledRun& run) const { return MaxLive(run) && MaxPureSpin(); }
    // Max dual race: both workers spin to every deadline on their own cores and the first to arrive submits (claim under the
    // queue lock, so a batch is sent once). A core held by an interrupt/DPC no longer makes the batch late. MACROHID_MAX_NORACE=1 off.
    static bool MaxRace() { static const bool on = !MaxEnv(L"MACROHID_MAX_NORACE"); return on; }
    bool Races(const ScheduledRun& run) const { return SpinsThrough(run) && MaxRace(); }
    // Realtime class for the whole process while a Max profile is warm (1.6.0 did this for 极致 per run); restored otherwise.
    void ApplyProcessClass(bool max)
    {
        HANDLE process = GetCurrentProcess();
        if (max && !realtimeApplied && MaxRealtime())
        {
            previousPriorityClass = GetPriorityClass(process);
            realtimeApplied = SetPriorityClass(process, REALTIME_PRIORITY_CLASS) != FALSE;
        }
        else if (!max && realtimeApplied)
        {
            SetPriorityClass(process, previousPriorityClass ? previousPriorityClass : NORMAL_PRIORITY_CLASS);
            realtimeApplied = false;
        }
    }
    // A priority-31 thread that never yields starves the kernel's per-processor worker threads on its core, which can stall
    // the whole system. Max spins through every wait but, after 50 ms of uninterrupted spinning, rests 0.5 ms on the timer
    // when the next deadline is more than 3 ms away, so no deadline is moved.
    struct Breather
    {
        int64_t frequency, spinStart;
        explicit Breather(int64_t f) : frequency(f), spinStart(QueryCounter()) {}
        bool Due(int64_t now, int64_t remaining) const
        {
            return now - spinStart > frequency / 20 && remaining > frequency * 3 / 1000;
        }
        void Rest(HANDLE timer, HANDLE wake)
        {
            LARGE_INTEGER due{}; due.QuadPart = -5000; // 0.5 ms
            if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
            {
                if (wake) { HANDLE handles[]{wake, timer}; WaitForMultipleObjects(2, handles, FALSE, 5); }
                else WaitForSingleObject(timer, 5);
            }
            else Sleep(1);
            spinStart = QueryCounter();
        }
    };

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
    int64_t Due(ScheduledRun& run) { return DueAt(run, run.next); }
    int64_t DueAt(ScheduledRun& run, size_t index)
    {
        const auto& batch = run.plan->batches[index];
        // The lead moves only the written time. Release dependencies and chord holds are applied after it, from the
        // measured start of the press, so a compensated hold is never shorter than written.
        int64_t due = run.start + batch.dueTicks + TotalPausedTicks(run.control) - SendLead(run, index);
        for (const auto& dependency : run.plan->releaseDependencies[index])
            due = std::max(due, run.submittedAt[dependency.first] + dependency.second);
        return run.chord.AdjustDue(due,
            run.plan->chordEdges[index], ModifierChordHoldTicks(run.frequency));
    }
    bool CanPumpSingleRun(ScheduledRun& run)
    {
        if (!singleRunFastPath) return false;
        // Max spins to every deadline on the owner's pinned core, so there is no timer wake to rescue.
        if (Races(run)) return false;
        if (SpinsThrough(run)) return true;
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
    // Max lock-free race (single run): both workers spin to the next deadline on their own cores; the first to get there takes
    // the batch with one compare-exchange and submits it, without the queue lock, so an interrupt on one core cannot delay the
    // batch and neither worker ever waits on the other. Ends when the run is done, stopped or paused, or another run is queued
    // (CloseRace leaves run.next consistent for the locked path).
    std::atomic<size_t> runCount{0};
    static constexpr size_t RaceClosed = SIZE_MAX;
    static void CloseRace(ScheduledRun& run)
    {
        while (true)
        {
            size_t c = run.claimed.load(std::memory_order_acquire);
            if (c == RaceClosed) return;
            // Only close between batches: an in-flight submission publishes first.
            if (run.published.load(std::memory_order_acquire) == c
                && run.claimed.compare_exchange_strong(c, RaceClosed, std::memory_order_acq_rel)) return;
            PauseProcessor();
        }
    }
    bool RaceShouldEnd(ScheduledRun& run, size_t index)
    {
        return index >= run.plan->batches.size() || run.status != MhpOk || run.IsStopped() || IsPaused(run.control)
            || stopping || runCount.load(std::memory_order_acquire) > 1 || PowerHeld();
    }
    void RaceSingleRun(ScheduledRun& run, int worker, HANDLE timer, bool priorityApplied, bool mmcss)
    {
        Breather breather(run.frequency);
        while (true)
        {
            const size_t i = run.published.load(std::memory_order_acquire);
            const size_t c = run.claimed.load(std::memory_order_acquire);
            if (c == RaceClosed) return;
            if (RaceShouldEnd(run, i)) { CloseRace(run); return; }
            if (c != i) { PauseProcessor(); continue; } // the other worker is submitting batch i
            const int64_t target = DueAt(run, i);
            int64_t now = QueryCounter();
            bool moved = false;
            while (now < target)
            {
                if (run.claimed.load(std::memory_order_acquire) != i || RaceShouldEnd(run, i)) { moved = true; break; }
                if (breather.Due(now, target - now)) breather.Rest(timer, nullptr);
                else PauseProcessor();
                now = QueryCounter();
            }
            if (moved) continue;
            size_t expected = i;
            if (!run.claimed.compare_exchange_strong(expected, i + 1, std::memory_order_acq_rel)) continue;
            run.next = i;
            SubmitClaimedBatch(run, worker, priorityApplied, mmcss, true, target);
            run.published.store(i + 1, std::memory_order_release);
        }
    }
    void PumpSingleRun(ScheduledRun& run, int worker, HANDLE timer, bool priorityApplied, bool mmcss, uint64_t seen)
    {        const int64_t baseSpinTicks = run.frequency * (run.options.precisionMode == MhpUltraLowJitter ? 1000 : 300) / 1000000;
        int64_t spinTicks = baseSpinTicks;
        const int64_t maximumSpinTicks = run.frequency * (run.options.precisionMode == MhpUltraLowJitter ? 1000 : 800) / 1000000;
        const int64_t timerMargin = run.frequency * 50 / 1000000;
        const bool max = SpinsThrough(run);
        Breather breather(run.frequency);
        while (run.next < run.plan->batches.size() && run.status == MhpOk)
        {
            if (generation.load(std::memory_order_acquire) != seen || run.IsStopped() || IsPaused(run.control)) break;
            if (!CanPumpSingleRun(run)) break;
            const int64_t target = Due(run);
            const int64_t remaining = target - QueryCounter();
            if (max)
            {
                // Max: no timer sleep (its wake-up error is the jitter being removed); spin to every deadline.
                int64_t now = QueryCounter();
                while (now < target && generation.load(std::memory_order_acquire) == seen && !run.IsStopped())
                {
                    if (breather.Due(now, target - now)) breather.Rest(timer, changed[worker]);
                    else PauseProcessor();
                    now = QueryCounter();
                }
                if (generation.load(std::memory_order_acquire) != seen || run.IsStopped() || IsPaused(run.control)) break;
                SubmitClaimedBatch(run, worker, priorityApplied, mmcss, true, target);
                continue;
            }
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
        bool lastMax = false;
        DWORD_PTR lastMask = 0;
        auto configure = [&](int mode, DWORD_PTR mask, bool max)
        {
            max = max && !PowerHeld(); // power guard: no TIME_CRITICAL / MMCSS CRITICAL around standby
            // 极限 pins each worker to its selected core. Other profiles float, but only over the quiet set measured by the
            // stall probe (interrupt-heavy cores excluded) when one is known: a soft restriction, not a single-core pin.
            const DWORD_PTR effective = mode == MhpUltraLowJitter ? mask : g_quietCoreMask.load(std::memory_order_acquire);
            if (lastMode != mode || lastMax != max)
            {
                if (mmcss) { AvRevertMmThreadCharacteristics(mmcss); mmcss = nullptr; }
                // Max: time-critical (31 in the realtime class). Others: HIGHEST.
                SetThreadPriority(GetCurrentThread(), max ? THREAD_PRIORITY_TIME_CRITICAL : THREAD_PRIORITY_HIGHEST);
            }
            if (mode == MhpUltraLowJitter && !mmcss)
            {
                DWORD task = 0;
                mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
                if (mmcss) AvSetMmThreadPriority(mmcss, max ? AVRT_PRIORITY_CRITICAL : AVRT_PRIORITY_HIGH);
            }
            if (lastMode != mode || lastMask != effective)
            {
                if (oldAffinity) { SetThreadAffinityMask(GetCurrentThread(), oldAffinity); oldAffinity = 0; }
                if (effective) oldAffinity = SetThreadAffinityMask(GetCurrentThread(), effective);
            }
            lastMode = mode; lastMask = effective; lastMax = max;
        };
        // Prepared means timer, priority, MMCSS and affinity are ready before
        // the first deadline, not just that the std::thread has been created.
        configure(warmPrecision.load(std::memory_order_acquire), cores[worker].mask, warmMax.load(std::memory_order_acquire));
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
            bool race = false;
            {
                std::lock_guard<std::mutex> lock(gate);
                if (stopping && runs.empty()) break;
                for (auto& run : runs)
                {
                    // A run being raced is in flight for the locked path, but the other worker may still join the race.
                    if (run->done || (run->inFlight && !(run->racers > 0 && runs.size() == 1))) continue;
                    if (run->racers > 0) { picked = run; target = 0; break; }
                    if (stopping || run->IsStopped()) { Finish(*run, MhpCancelled); continue; }
                    if (run->next == run->plan->batches.size()) { Finish(*run, MhpOk); continue; }
                    if (IsPaused(run->control)) continue;
                    if (runs.size() == 1 && CanPumpSingleRun(*run) && run->owner != worker) continue;
                    if (run->options.precisionMode != MhpUltraLowJitter && run->owner != worker) continue;
                    int64_t due = Due(*run);
                    if (run->owner != worker && !Races(*run)) due += std::max<int64_t>(1, run->frequency * 2 / 1000000);
                    if (due < target) { target = due; picked = run; }
                }
                for (auto it = runs.begin(); it != runs.end() && retiredCount < retired.size(); )
                {
                    if ((*it)->done) { retired[retiredCount++] = std::move(*it); it = runs.erase(it); }
                    else ++it;
                }
                if (stopping && runs.empty()) break;
                runCount.store(runs.size(), std::memory_order_release);
                seen = generation.load(std::memory_order_acquire);
                race = runs.size() == 1 && picked && Races(*picked) && !picked->done && !picked->IsStopped() && !IsPaused(picked->control)
                    && picked->next < picked->plan->batches.size() && (picked->racers > 0 || !picked->inFlight);
                if (race)
                {
                    if (picked->racers++ == 0)
                    {
                        // First racer: the race starts from the locked path's position.
                        picked->inFlight = true;
                        picked->published.store(picked->next, std::memory_order_release);
                        picked->claimed.store(picked->next, std::memory_order_release);
                    }
                }
                exclusive = !race && runs.size() == 1 && picked && CanPumpSingleRun(*picked) && picked->owner == worker;
                if (exclusive) picked->inFlight = true;
            }
            for (size_t i = 0; i < retiredCount; i++) retired[i].reset();
            if (race)
            {
                configure(picked->options.precisionMode, picked->cores[worker].mask, true);
                RaceSingleRun(*picked, worker, timer, priorityApplied, mmcss != nullptr);
                {
                    std::lock_guard<std::mutex> lock(gate);
                    if (--picked->racers == 0)
                    {
                        picked->next = picked->published.load(std::memory_order_acquire);
                        picked->inFlight = false;
                        if (picked->status != MhpOk) Finish(*picked, picked->status);
                        else if (picked->IsStopped() || stopping) Finish(*picked, MhpCancelled);
                        else if (picked->next == picked->plan->batches.size()) Finish(*picked, MhpOk);
                    }
                }
                Notify();
                continue;
            }
            if (picked && picked->IsStopped() && !exclusive) continue;
            if (!picked)
            {
                if (retiredCount) continue; // Return large buffers outside the queue lock.
                // Keep the prepared thread state while sleeping. Re-entering
                // MMCSS/affinity on every isolated hotkey is avoidable startup
                // work; mode changes and shutdown restore it below. A warm-up
                // for another profile wakes the workers so it is applied here.
                configure(warmPrecision.load(std::memory_order_acquire), cores[worker].mask, warmMax.load(std::memory_order_acquire));
                if (hotStandby.load(std::memory_order_acquire) && !PowerHeld())
                {
                    // Max hot standby: the owner of the next run is already running on its core, so a hotkey's first
                    // batch does not wait for the scheduler to wake this thread. Polls the generation; rests briefly.
                    Breather breather(QueryFrequency());
                    while (generation.load(std::memory_order_acquire) == seen && hotStandby.load(std::memory_order_acquire) && !PowerHeld())
                    {
                        if (breather.Due(QueryCounter(), INT64_MAX)) breather.Rest(timer, changed[worker]);
                        else PauseProcessor();
                    }
                    continue;
                }
                WaitForSingleObject(changed[worker], INFINITE);
                continue;
            }
            const auto mask = picked->cores[worker].mask;
            configure(picked->options.precisionMode, mask, IsMax(*picked));
            if (exclusive)
            {
                PumpSingleRun(*picked, worker, timer, priorityApplied, mmcss != nullptr, seen);
                continue;
            }
            const int64_t spinUs = picked->next == 0 ? 1000 : picked->options.precisionMode == MhpUltraLowJitter ? 1000 : 300;
            int64_t remaining = target - QueryCounter();
            if (!SpinsThrough(*picked) && remaining > picked->frequency * (spinUs + 50) / 1000000)
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
            {
                Breather breather(picked->frequency);
                const bool max = SpinsThrough(*picked);
                int64_t now = QueryCounter();
                while (now < target && generation.load(std::memory_order_acquire) == seen && !picked->IsStopped())
                {
                    if (max && breather.Due(now, target - now)) breather.Rest(timer, changed[worker]);
                    else PauseProcessor();
                    now = QueryCounter();
                }
            }
            if (generation.load(std::memory_order_acquire) != seen) continue;
            int64_t due;
            {
                std::lock_guard<std::mutex> lock(gate);
                if (picked->done || picked->inFlight || picked->IsStopped() || IsPaused(picked->control)) continue;
                due = Due(*picked);
                const auto claimDue = due + (worker == picked->owner || Races(*picked) ? 0 : std::max<int64_t>(1, picked->frequency * 2 / 1000000));
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
    // Max SMT parking: a time-critical pause loop on the hyper-threading sibling of each worker core, while Max is warm and a
    // Max hotkey listens or a run is active, so no other thread (the game) shares the physical core with a worker.
    // MACROHID_MAX_PARK=1 enables it (off by default: in the A/B it lowered some maxima but raised P99.9 at 1 ms spacing).
    std::vector<std::thread> parkers;
    std::atomic<bool> parkersStop{false};
    static bool MaxPark() { static const bool on = MaxEnv(L"MACROHID_MAX_PARK"); return on; }
    void StartParkers()
    {
        if (!parkers.empty() || !MaxPark()) return;
        std::vector<uint32_t> siblings;
        for (const auto& core : cores)
        {
            if (!core.mask) continue;
            for (uint32_t bit = 0; bit < sizeof(DWORD_PTR) * 8; bit++)
            {
                const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
                if (mask == core.mask) continue;
                DWORD_PTR process = 0, system = 0;
                if (!GetProcessAffinityMask(GetCurrentProcess(), &process, &system) || !(process & mask)) continue;
                if (LookupLogicalCore(bit).physicalCoreId == core.physicalCoreId) siblings.push_back(bit);
            }
        }
        parkersStop.store(false, std::memory_order_release);
        for (auto bit : siblings)
        {
            parkers.emplace_back([this, bit]
            {
                SetThreadAffinityMask(GetCurrentThread(), static_cast<DWORD_PTR>(1) << bit);
                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
                HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                Breather breather(QueryFrequency());
                while (!parkersStop.load(std::memory_order_acquire))
                {
                    if (PowerHeld() || (!hotStandby.load(std::memory_order_acquire) && g_playbackActive.load(std::memory_order_acquire) == 0))
                    {
                        Sleep(2);
                        breather = Breather(QueryFrequency());
                        continue;
                    }
                    if (breather.Due(QueryCounter(), INT64_MAX)) breather.Rest(timer, nullptr);
                    else PauseProcessor();
                }
                if (timer) CloseHandle(timer);
            });
        }
    }
    void StopParkers()
    {
        parkersStop.store(true, std::memory_order_release);
        for (auto& parker : parkers) if (parker.joinable()) parker.join();
        parkers.clear();
    }
public:
    // Process exit / DLL detach: no power unregistration under the loader lock (waiting for a timer callback could deadlock).
    ~SharedPlaybackScheduler() { Shutdown(false); }
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
        const auto status = WarmCore(options, stats, allowedMask);
        // Parkers follow the warm Max profile and its pinned cores (restarted when the cores change).
        std::lock_guard<std::mutex> life(lifecycle);
        StopParkers();
        if (status == MhpOk && warmMax.load(std::memory_order_acquire) && ready) StartParkers();
        return status;
    }
    MhpStatus WarmCore(const MhpRunOptions* options, MhpRunStats* stats, DWORD_PTR allowedMask)
    {
        std::lock_guard<std::mutex> life(lifecycle);
        if (stats) *stats = {};
        RegisterPower();
        const int requestedPrecision = options ? options->precisionMode : 1;
        const bool requestedMax = options && (options->flags & MhpFlagMaxPreemption) && requestedPrecision == MhpUltraLowJitter;
        bool alreadyReady;
        bool precisionChanged = false;
        { std::lock_guard<std::mutex> lock(gate); alreadyReady = ready;
          ApplyProcessClass(requestedMax && !PowerHeld());
          if (!requestedMax) hotStandby.store(false, std::memory_order_release);
          const bool maxChanged = warmMax.exchange(requestedMax, std::memory_order_acq_rel) != requestedMax;
          if (ready) precisionChanged = warmPrecision.exchange(requestedPrecision, std::memory_order_acq_rel) != requestedPrecision || maxChanged; }
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
          run->stats.selectedWorker0MaxLateUs = run->cores[0].maxLateUs; run->stats.selectedWorker1MaxLateUs = run->cores[1].maxLateUs; runs.push_back(run); runCount.store(runs.size(), std::memory_order_release); g_playbackActive.fetch_add(1, std::memory_order_acq_rel); }
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
    // Max hot standby on/off (only while a Max profile is warm). Turning it off wakes the spinning workers to sleep.
    bool SetHotStandby(bool on)
    {
        if (on && !warmMax.load(std::memory_order_acquire)) return false;
        hotStandby.store(on, std::memory_order_release);
        Notify();
        return true;
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
    void Shutdown(bool unregisterPower = true)
    {
        if (unregisterPower) UnregisterPower();
        std::lock_guard<std::mutex> life(lifecycle);
        StopParkers();
        hotStandby.store(false, std::memory_order_release);
        { std::lock_guard<std::mutex> lock(gate); ready = false; stopping = true; ApplyProcessClass(false); warmMax.store(false); }
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
