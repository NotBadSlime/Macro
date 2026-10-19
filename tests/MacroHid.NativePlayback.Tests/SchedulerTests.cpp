#define MACROHID_PLAYBACK_TEST
#include "../../src/native/MacroHid.NativePlayback/NativePlayback.cpp"
#include <iostream>
#include <stdexcept>
#include <chrono>
#include <fstream>

void Check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
// A release is timed from the START of its press call, so the hold seen inside the calls can differ from the written one by
// the call-entry skew between the two calls (measured at 0.1 to 0.2 us here). The slack allows that and nothing more:
// the old bug lengthened holds by a whole SendInput call, hundreds of microseconds.
constexpr int64_t kHoldSlackUs = 20;
// Match the managed playback session's timer request without using legacy
// PrecisionScope (which changes process priority).
struct ProbeTimerResolution
{
    NtSetTimerResolutionFn set = reinterpret_cast<NtSetTimerResolutionFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtSetTimerResolution"));
    ULONG actual = 0;
    bool nt = set && set(5000, TRUE, &actual) == 0;
    bool mm = !nt && timeBeginPeriod(1) == TIMERR_NOERROR;
    ~ProbeTimerResolution() { if (nt) { ULONG ignored; set(actual, FALSE, &ignored); } if (mm) timeEndPeriod(1); }
};
std::atomic<int> calls{0}, inputsSent{0}, partial{0};
std::array<std::atomic<int64_t>, 2> firstCalls{};
std::atomic<bool> blockSubmit{false}, insideSubmit{false};
std::atomic<int> slowFirstCallUs{0}; // when > 0, the first call of a run takes this long, like a slow SendInput
std::atomic<int> slowEveryCallUs{0}; // when > 0, every call takes this long (SendInput cost learning)
UINT FakeInput(UINT count, INPUT*)
{
    int call = calls.fetch_add(1); if (call < 2) firstCalls[call] = QueryCounter();
    insideSubmit = true;
    const int slowUs = slowEveryCallUs > 0 ? slowEveryCallUs.load() : call == 0 ? slowFirstCallUs.load() : 0;
    if (slowUs > 0)
    {
        // Spin, do not Sleep: Sleep(2) often lasts 10+ ms, and a press call longer than the hold would legitimately
        // delay the release (a run submits strictly one batch at a time), which would make the test measure the wrong thing.
        const int64_t until = QueryCounter() + QueryFrequency() * slowUs / 1000000;
        while (QueryCounter() < until) YieldProcessor();
    }
    while (blockSubmit) Sleep(1);
    const UINT sent = partial ? count - 1 : count;
    inputsSent += sent;
    SetLastError(0); // SendInput may return zero without a useful last-error value.
    return sent;
}
struct Run
{
    void* handle = nullptr;
    HANDLE done = nullptr;
    Run(const std::vector<MhpBatch>& batches, MhpRunOptions options, void* control = nullptr, int inputCount = 1, int64_t origin = 0, bool releaseSecond = false)
    {
        std::vector<INPUT> inputs(inputCount);
        for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
        if (releaseSecond && inputs.size() > 1) inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        void* plan = nullptr;
        Check(MhpCreatePlan(inputs.data(), static_cast<uint32_t>(inputs.size()), batches.data(), static_cast<uint32_t>(batches.size()), &plan) == MhpOk, "create");
        auto status = MhpSubmitPlanAt(plan, &options, control, origin, &handle, &done);
        MhpDestroyPlan(plan); // Async run must retain the plan even if the caller releases it now.
        Check(status == MhpOk, "submit");
    }
    MhpRunStats Wait(MhpStatus expected = MhpOk, DWORD timeout = 3000)
    {
        Check(WaitForSingleObject(done, timeout) == WAIT_OBJECT_0, "completion timeout");
        MhpRunStats stats{};
        Check(MhpGetRunResult(handle, &stats) == expected, "wrong result");
        return stats;
    }
    ~Run() { MhpReleaseRun(handle); }
};
#include "ReceiverProbe.h"
int main(int argc, char** argv)
{
    try
    {
        g_testSubmit = FakeInput;
        MhpRunOptions options{}; options.precisionMode = MhpUltraLowJitter; options.qpcFrequency = QueryFrequency();
        options.outlierThresholdUs = 100;
        if (argc == 2 && std::string(argv[1]) == "--batch-trace")
        {
            // BatchTraceEnabled() latches on first use, so this mode runs in its own process.
            SetEnvironmentVariableW(L"MACROHID_BATCH_TRACE", L"1");
            auto ms = [&](int value) { return options.qpcFrequency * value / 1000; };
            Check(MhpWarmEngine(&options, nullptr) == MhpOk, "trace warm");
            Run traced({{0,0,1,1},{ms(5),0,1,1},{ms(10),0,1,1}}, options);
            Check(traced.Wait().batchesSubmitted == 3, "trace run");
            MhpBatchTrace records[3]{}; uint32_t written = 0;
            Check(MhpGetBatchTrace(traced.handle, records, 3, &written) == MhpOk && written == 3, "trace count");
            for (uint32_t i = 0; i < 3; i++)
            {
                const auto& r = records[i];
                Check(r.batchIndex == i && r.inputCount == 1 && r.sent == 1 && r.error == 0, "trace identity");
                Check(r.nominalDue == r.effectiveDue, "no pause or dependency: effective due equals nominal");
                Check(r.submitBegin >= r.effectiveDue - ms(1) && r.submitEnd >= r.submitBegin, "trace timestamps");
                if (i) Check(r.nominalDue - records[i - 1].nominalDue == ms(5), "trace due spacing");
            }
            MhpBatchTrace two[2]{}; uint32_t total = 0;
            Check(MhpGetBatchTrace(traced.handle, two, 2, &total) == MhpOk && total == 3 && two[1].batchIndex == 1, "capacity-limited copy reports full count");
            Check(MhpGetBatchTrace(traced.handle, nullptr, 0, &total) == MhpOk && total == 3, "size query");
            Check(MhpGetBatchTrace(traced.handle, nullptr, 1, &total) == MhpInvalidArgument, "null buffer rejected");
            MhpShutdownEngine();
            std::cout << "PASS: batch trace\n";
            return 0;
        }
        if (argc == 6 && std::string(argv[1]) == "--receiver-benchmark")
        {
            options.precisionMode = std::stoi(argv[2]);
            RunReceiverBenchmark(options, std::stoi(argv[3]), std::stoi(argv[4]), argv[5]);
            MhpShutdownEngine();
            return 0;
        }
        if ((argc == 6 || argc == 7) && std::string(argv[1]) == "--single-benchmark")
        {
            ProbeTimerResolution timer;
            options.precisionMode = std::stoi(argv[2]);
            const int count = std::stoi(argv[3]), intervalUs = std::stoi(argv[4]);
            Check(options.precisionMode == 1 || options.precisionMode == 2, "invalid mode");
            Check(count > 0 && count <= 1000000 && intervalUs >= 0 && intervalUs <= 100000, "invalid workload");
            options.enableCpuScan = options.precisionMode == 2;
            const uint64_t allowedMask = argc == 7 ? std::stoull(argv[6], nullptr, 0) : 0;
            Check(MhpPrepareEngine(&options, allowedMask, nullptr) == MhpOk, "single benchmark warm");
            std::vector<MhpBatch> batches;
            for (int i = 0; i < count; i++) batches.push_back({i * options.qpcFrequency * intervalUs / 1000000,0,1,1});
            INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = 'A';
            void* plan = nullptr;
            Check(MhpCreatePlan(&input, 1, batches.data(), count, &plan) == MhpOk, "single benchmark plan");
            // Reuse one prepared plan, including its preallocated diagnostic workspace.
            std::vector<MhpOutlierEvent> trace(count);
            options.outlierThresholdUs = -1; options.outlierEvents = trace.data(); options.outlierEventCapacity = count;
            FILETIME created{}, exited{}, kernelBefore{}, userBefore{}, kernelAfter{}, userAfter{};
            GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernelBefore, &userBefore);
            auto start = QueryCounter();
            void* handle = nullptr; HANDLE done = nullptr;
            Check(MhpSubmitPlanAt(plan, &options, nullptr, start, &handle, &done) == MhpOk, "single benchmark submit");
            Check(WaitForSingleObject(done, static_cast<DWORD>(count * static_cast<int64_t>(intervalUs) / 1000 + 10000)) == WAIT_OBJECT_0, "single benchmark completion");
            auto end = QueryCounter();
            GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernelAfter, &userAfter);
            MhpRunStats stats{}; MhpRunTrace runTrace{};
            Check(MhpGetRunResult(handle, &stats) == MhpOk && stats.batchesSubmitted == static_cast<uint32_t>(count), "single benchmark missing events");
            Check(MhpGetRunTrace(handle, &runTrace) == MhpOk, "single benchmark trace");
            std::ofstream file(argv[5]);
            Check(file.good(), "single benchmark output");
            file << "run,batch,due_qpc,submit_qpc,frequency,late_us\n";
            for (uint32_t i = 0; i < stats.outlierEventsWritten; i++)
            {
                const auto& sample = trace[i];
                file << "0," << sample.batchIndex << ',' << sample.dueTick << ',' << sample.actualTick << ',' << options.qpcFrequency << ',' << sample.lateUs << '\n';
            }
            auto ticks = [](FILETIME value) { return (static_cast<uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime; };
            const double cpuMs = (ticks(kernelAfter) + ticks(userAfter) - ticks(kernelBefore) - ticks(userBefore)) / 10000.0;
            std::cout << "fake input; single events=" << count << " interval_us=" << intervalUs
                << " p999_us=" << stats.p999LateUs << " max_us=" << stats.maxLateUs
                << " startup_us=" << ToMicroseconds(runTrace.firstSubmit - start, options.qpcFrequency)
                << " cpu_ms=" << cpuMs << " wall_ms=" << ToMicroseconds(end - start, options.qpcFrequency) / 1000.0
                << " owner_batches=" << stats.primaryWorkerWins << " helper_batches=" << stats.helperWorkerWins
                << " selected_cpu=" << stats.selectedCpuSet << " worker0=" << stats.selectedWorker0 << " worker1=" << stats.selectedWorker1 << "\n";
            MhpReleaseRun(handle); MhpDestroyPlan(plan); MhpShutdownEngine();
            return 0;
        }
        if ((argc == 4 || argc == 5) && std::string(argv[1]) == "--submit-benchmark")
        {
            // Many isolated single-batch runs, the way hotkeys arrive: submit-to-first-call latency and
            // per-run submit cost. Fake input, no SendInput. Optional argv[4] = gap between runs in ms.
            ProbeTimerResolution timer;
            options.precisionMode = std::stoi(argv[2]);
            const int count = std::stoi(argv[3]);
            const int gapMs = argc == 5 ? std::stoi(argv[4]) : 2;
            Check(count >= 10 && count <= 100000, "invalid count");
            options.enableCpuScan = 0;
            Check(MhpPrepareEngine(&options, 0, nullptr) == MhpOk, "submit benchmark warm");
            INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = 'A';
            MhpBatch batch{0, 0, 1, 1};
            void* plan = nullptr;
            Check(MhpCreatePlan(&input, 1, &batch, 1, &plan) == MhpOk, "submit benchmark plan");
            std::vector<double> startup, submitCost;
            for (int i = 0; i < count; i++)
            {
                Sleep(gapMs);
                calls = 0;
                const auto before = QueryCounter();
                void* handle = nullptr; HANDLE done = nullptr;
                Check(MhpSubmitPlanAt(plan, &options, nullptr, before, &handle, &done) == MhpOk, "submit benchmark submit");
                const auto after = QueryCounter();
                Check(WaitForSingleObject(done, 3000) == WAIT_OBJECT_0, "submit benchmark completion");
                startup.push_back((firstCalls[0] - before) * 1e6 / options.qpcFrequency);
                submitCost.push_back((after - before) * 1e6 / options.qpcFrequency);
                MhpReleaseRun(handle);
            }
            MhpDestroyPlan(plan);
            auto pct = [](std::vector<double> v, double q) { std::sort(v.begin(), v.end()); return v[std::min(v.size() - 1, static_cast<size_t>(q * v.size()))]; };
            std::cout << "submit benchmark mode=" << options.precisionMode << " runs=" << count
                << " startup_us p50=" << pct(startup, .5) << " p99=" << pct(startup, .99) << " p999=" << pct(startup, .999)
                << " submit_us p50=" << pct(submitCost, .5) << " p99=" << pct(submitCost, .99) << "\n";
            MhpShutdownEngine();
            return 0;
        }
        if (argc == 5 && std::string(argv[1]) == "--benchmark")
        {
            ProbeTimerResolution timer;
            options.precisionMode = std::stoi(argv[2]);
            Check(options.precisionMode == 1 || options.precisionMode == 2, "benchmark supports native balanced/extreme");
            const int count = std::stoi(argv[3]);
            Check(count >= 1 && count <= 1000000, "invalid count");
            options.enableCpuScan = options.precisionMode == 2;
            Check(MhpWarmEngine(&options, nullptr) == MhpOk, "benchmark warm");
            std::vector<MhpBatch> batches;
            for (int i = 0; i < count; i++) batches.push_back({i * options.qpcFrequency / 1000,0,1,1});
            std::vector<MhpOutlierEvent> traceA(count), traceB(count);
            options.outlierThresholdUs = -1; options.outlierEventCapacity = count; options.outlierEvents = traceA.data();
            Run a(batches, options);
            options.outlierEvents = traceB.data();
            Run b(batches, options);
            auto statsA = a.Wait(MhpOk, count + 10000), statsB = b.Wait();
            std::ofstream file(argv[4]);
            file << "run,batch,due_qpc,submit_qpc,frequency,late_us\n";
            for (int run = 0; run < 2; run++)
                for (const auto& sample : run == 0 ? traceA : traceB)
                    file << run << ',' << sample.batchIndex << ',' << sample.dueTick << ',' << sample.actualTick << ',' << options.qpcFrequency << ',' << sample.lateUs << '\n';
            Check(statsA.batchesSubmitted == static_cast<uint32_t>(count) && statsB.batchesSubmitted == static_cast<uint32_t>(count), "missing events");
            std::cout << "fake input; events=" << count * 2 << " p999_us=" << statsA.p999LateUs << "/" << statsB.p999LateUs
                << " max_us=" << statsA.maxLateUs << "/" << statsB.maxLateUs << "\n";
            MhpShutdownEngine();
            return 0;
        }
        Check(MhpWarmEngine(&options, nullptr) == MhpOk, "warm");
        auto ms = [&](int value) { return options.qpcFrequency * value / 1000; };
        {
            // Stall classification: interrupt-heavy cores are noisy, quiet ones are not; the penalty ranks noisy last.
            CoreChoice quiet{}; quiet.mask = 2; quiet.maxStallUs = 1500; quiet.stallsOver50Us = 3; quiet.stallWindowMs = 1000;
            CoreChoice manyGaps{}; manyGaps.mask = 4; manyGaps.maxStallUs = 150; manyGaps.stallsOver50Us = 400; manyGaps.stallWindowMs = 1000;
            CoreChoice longStall{}; longStall.mask = 8; longStall.maxStallUs = 8700; longStall.stallsOver50Us = 7; longStall.stallWindowMs = 1000;
            CoreChoice fewLong{}; fewLong.mask = 16; fewLong.maxStallUs = 300; fewLong.stallsOver200Us = 30; fewLong.stallWindowMs = 1000;
            // Rates: 30 gaps in 60 ms is 500/s (noisy); 30 gaps in 1 s is not.
            CoreChoice shortBusy{}; shortBusy.mask = 32; shortBusy.maxStallUs = 120; shortBusy.stallsOver50Us = 30; shortBusy.stallWindowMs = 60;
            CoreChoice longQuiet = shortBusy; longQuiet.stallWindowMs = 1000;
            Check(!IsNoisyCore(quiet) && !IsNoisyCore(longQuiet), "quiet cores (one 1.5 ms event, low rate) are not noisy");
            Check(IsNoisyCore(manyGaps) && IsNoisyCore(longStall) && IsNoisyCore(fewLong) && IsNoisyCore(shortBusy), "noisy cores detected");
            Check(StallPenalty(quiet) < StallPenalty(manyGaps) && StallPenalty(quiet) < StallPenalty(longStall), "noisy ranks last");
            // Real probe on this machine: every core reports a result, nothing is left at the sentinel value.
            std::vector<MhpCoreStall> stalls(64); uint32_t written = 0;
            Check(MhpScanCoreStalls(0, 20, stalls.data(), 64, &written, nullptr) == MhpOk && written > 0, "stall probe");
            for (uint32_t i = 0; i < written; i++) Check(stalls[i].maxStallUs >= 0 && stalls[i].maxStallUs < 1000000, "stall value");
            std::cout << "stall_probe_cores=" << written << "\n";
        }
        {
            // Relative moves are sent alone after their edge check; neighbouring keys keep one call.
            INPUT mixed[6]{};
            for (auto& in : mixed) { in.type = INPUT_KEYBOARD; in.ki.wVk = 'A'; }
            for (int at : {2, 4, 5}) { mixed[at] = {}; mixed[at].type = INPUT_MOUSE; mixed[at].mi.dwFlags = MOUSEEVENTF_MOVE; mixed[at].mi.dx = 5; }
            std::vector<UINT> sizes; int recentered = 0;
            UINT total = SubmitSplittingRelativeMoves(6, mixed, [&](UINT n, INPUT*) { sizes.push_back(n); return n; },
                [&](int, int) { recentered++; });
            Check(total == 6 && recentered == 3, "mixed batch sent fully, one edge check per move");
            Check((sizes == std::vector<UINT>{2, 1, 1, 1, 1}), "keys grouped, moves alone");
            sizes.clear();
            total = SubmitSplittingRelativeMoves(6, mixed, [&](UINT n, INPUT*) { sizes.push_back(n); return n == 1 ? 0u : n; },
                [](int, int) {});
            Check(total == 2 && sizes.size() == 2, "stops at the first short send");
        }
        // Re-warming for another profile while idle re-configures the workers and must leave them usable.
        for (int mode : {1, 2, 1, 2})
        {
            options.precisionMode = mode;
            Check(MhpPrepareEngine(&options, 0, nullptr) == MhpOk, "profile re-warm");
            calls = 0;
            Run rewarmed({{0,0,1,1},{ms(2),0,1,1}}, options);
            Check(rewarmed.Wait().batchesSubmitted == 2 && calls == 2, "run after profile re-warm");
        }
        options.precisionMode = MhpUltraLowJitter;
        for (int mode : {1, 2})
        {
            // Pause after the single-run path has started; neither the owner
            // nor its deadline rescue worker may submit while it is paused.
            calls = 0;
            options.precisionMode = mode;
            auto control = MhpCreatePlaybackControl();
            Run active({{0,0,1,1},{ms(80),0,1,1}}, options, control);
            auto until = QueryCounter() + ms(2000);
            while (calls == 0 && QueryCounter() < until) Sleep(1);
            Check(calls == 1, "single lane did not start");
            MhpPausePlayback(control);
            Sleep(100);
            Check(calls == 1 && WaitForSingleObject(active.done, 0) == WAIT_TIMEOUT, "single lane ignored active pause");
            MhpResumePlayback(control);
            Check(active.Wait().batchesSubmitted == 2 && firstCalls[1] - firstCalls[0] >= ms(180), "single lane lost paused time");
            MhpDestroyPlaybackControl(control);
        }
        {
            // Reuse the same scratch lease. Stale timestamps from the prior run
            // must never shorten the release dependency of a late-started run.
            INPUT inputs[2]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
            MhpBatch batches[]{{0,0,1,1},{ms(3),1,1,1}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 2, batches, 2, &plan) == MhpOk, "retained single plan");
            for (int repeat = 0; repeat < 12; repeat++)
            {
                calls = 0;
                void* handle = nullptr; HANDLE done = nullptr;
                Check(MhpSubmitPlanAt(plan, &options, nullptr, QueryCounter() - ms(20), &handle, &done) == MhpOk, "retained single submit");
                Check(WaitForSingleObject(done, 2000) == WAIT_OBJECT_0, "retained single completion");
                MhpRunStats stats{};
                Check(MhpGetRunResult(handle, &stats) == MhpOk && stats.batchesSubmitted == 2
                    && ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency) >= 3000 - kHoldSlackUs, "retained single hold shortened");
                MhpReleaseRun(handle);
            }
            MhpDestroyPlan(plan);
        }
        {
            // A slow press call must not delay the release. The release is due 5 ms after the press STARTED; the hold the
            // user wrote is measured from there, not from when the press call returned. (The release dependency used to
            // start at the press's return, so a SendInput that took 2 ms made a 5 ms hold 7 ms.)
            // The press call must stay shorter than the hold: a run submits one batch at a time, so a longer call
            // legitimately delays the release.
            INPUT inputs[2]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
            MhpBatch batches[]{{0,0,1,1},{ms(5),1,1,1}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 2, batches, 2, &plan) == MhpOk, "slow press plan");
            for (int slowUs : {0, 600, 2000, 3500}) // all well inside the 5 ms hold; 600 us is the measured real key-down cost
            {
                slowFirstCallUs = slowUs;
                calls = 0;
                void* handle = nullptr; HANDLE done = nullptr;
                Check(MhpSubmitPlan(plan, &options, nullptr, &handle, &done) == MhpOk, "slow press submit");
                Check(WaitForSingleObject(done, 2000) == WAIT_OBJECT_0, "slow press completion");
                MhpRunStats stats{};
                Check(MhpGetRunResult(handle, &stats) == MhpOk && stats.batchesSubmitted == 2, "slow press run");
                MhpReleaseRun(handle);
                const auto between = ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency);
                std::cout << "slow_press_us=" << slowUs << " press_start_to_release_start_us=" << between << "\n";
                Check(between >= 5000 - kHoldSlackUs, "written hold shortened by a slow press");        // the guarantee that must stay
                Check(between < 5000 + 800, "slow press pushed the release later than written"); // the point of the change
            }
            slowFirstCallUs = 0;
            MhpDestroyPlan(plan);
        }
        {
            // SendInput cost compensation: after the scheduler learns a slow call (here 600 us for a key batch), a compensated
            // run starts each batch that much earlier, but a release still never comes sooner than the written hold after the
            // press actually started.
            INPUT inputs[2]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
            MhpBatch batches[]{{ms(3),0,1,1},{ms(8),1,1,1}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 2, batches, 2, &plan) == MhpOk, "compensation plan");
            auto run = [&](uint32_t flags, int64_t& pressLeadUs, int64_t& holdUs)
            {
                slowEveryCallUs = 600;
                calls = 0;
                auto o = options; o.flags = flags;
                const int64_t origin = QueryCounter();
                void* handle = nullptr; HANDLE done = nullptr;
                Check(MhpSubmitPlanAt(plan, &o, nullptr, origin, &handle, &done) == MhpOk, "compensation submit");
                Check(WaitForSingleObject(done, 2000) == WAIT_OBJECT_0, "compensation completion");
                MhpReleaseRun(handle);
                pressLeadUs = 3000 - ToMicroseconds(firstCalls[0] - origin, options.qpcFrequency);
                holdUs = ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency);
            };
            int64_t lead = 0, hold = 0;
            for (int i = 0; i < 40; i++) run(0, lead, hold); // fill the 64-sample window with slow calls (2 per run)
            int64_t keyUs = 0, mouseUs = 0; MhpGetSendCost(&keyUs, &mouseUs);
            std::cout << "learned_key_send_us=" << keyUs << "\n";
            Check(keyUs >= 500 && keyUs <= 800, "tracker learned the slow key call");
            run(0, lead, hold);
            Check(lead <= 50, "uncompensated run starts on the written time");
            run(MhpFlagCompensateSendInput, lead, hold);
            std::cout << "compensated_press_lead_us=" << lead << " hold_us=" << hold << "\n";
            Check(lead >= 450 && lead <= 900, "compensated press goes out about one send cost early");
            Check(hold >= 5000 - kHoldSlackUs, "compensation never shortens the written hold");
            slowEveryCallUs = 0;
            slowFirstCallUs = 0;
            MhpDestroyPlan(plan);
        }
        {
            // The earlier generations (2.0.x) count the hold from when the press call RETURNED, so a slow press pushes the
            // release later by exactly its own duration. The default generation (0, latest) must not. Same plan, same slow call.
            static_assert(sizeof(MhpRunOptions) == 48, "MhpRunOptions must keep its size: generation fills the existing tail padding");
            INPUT inputs[2]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
            MhpBatch batches[]{{0,0,1,1},{ms(5),1,1,1}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 2, batches, 2, &plan) == MhpOk, "generation plan");
            for (int generation : {0, 1, 3})
            {
                slowFirstCallUs = 2000;
                calls = 0;
                auto generationOptions = options; generationOptions.generation = static_cast<uint32_t>(generation);
                void* handle = nullptr; HANDLE done = nullptr;
                Check(MhpSubmitPlan(plan, &generationOptions, nullptr, &handle, &done) == MhpOk, "generation submit");
                Check(WaitForSingleObject(done, 2000) == WAIT_OBJECT_0, "generation completion");
                MhpRunStats stats{};
                Check(MhpGetRunResult(handle, &stats) == MhpOk && stats.batchesSubmitted == 2, "generation run");
                MhpReleaseRun(handle);
                const auto between = ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency);
                std::cout << "generation=" << generation << " slow_press_us=2000 press_start_to_release_start_us=" << between << "\n";
                if (generation == 0) Check(between < 5000 + 800, "latest generation let a slow press delay the release");
                else Check(between >= 5000 + 1500, "earlier generation no longer counts the hold from the press return");
            }
            slowFirstCallUs = 0;
            MhpDestroyPlan(plan);
        }
        {
            // Generation 2 (MacroHID 1.6.0) runs the plan on the older standby/inline engine through the synchronous entry point,
            // and never touches the shared scheduler. Fake input only: nothing reaches the desktop.
            INPUT inputs[2]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
            MhpBatch batches[]{{0,0,1,1},{ms(5),1,1,1}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 2, batches, 2, &plan) == MhpOk, "legacy plan");
            slowFirstCallUs = 0;
            for (int generation : {2, 0, 2})
            {
                calls = 0;
                auto legacyOptions = options; legacyOptions.generation = static_cast<uint32_t>(generation);
                legacyOptions.nativeEngineMode = MhpNativeEngineInline; // no standby threads needed: inline runs on the calling thread
                volatile long cancel = 0;
                MhpRunStats stats{};
                const auto status = MhpRunPlan(plan, &legacyOptions, &cancel, &stats);
                const auto between = ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency);
                std::cout << "run_generation=" << generation << " status=" << status << " batches=" << stats.batchesSubmitted << " between_us=" << between << "\n";
                Check(status == MhpOk && stats.batchesSubmitted == 2 && calls == 2, "plan did not run to completion");
                Check(between >= 5000 - kHoldSlackUs, "written interval shortened");
                Check(between < 5000 + 1500, "interval far longer than written");
            }
            MhpDestroyPlan(plan);
        }
        {
            // 1.6.0 submitted a batch one INPUT per call; later engines inject the batch in one call.
            INPUT inputs[3]{};
            for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = 'A'; }
            inputs[1].ki.wVk = 'B'; inputs[2].ki.wVk = 'C';
            MhpBatch batches[]{{0,0,3,3}};
            void* plan = nullptr;
            Check(MhpCreatePlan(inputs, 3, batches, 1, &plan) == MhpOk, "chord plan");
            calls = 0;
            auto legacyOptions = options; legacyOptions.generation = 2; legacyOptions.nativeEngineMode = MhpNativeEngineInline;
            volatile long cancel = 0;
            MhpRunStats stats{};
            Check(MhpRunPlan(plan, &legacyOptions, &cancel, &stats) == MhpOk && stats.nativeInputsSubmitted == 3, "1.6.0 chord run");
            std::cout << "v1_6_chord_calls=" << calls << "\n";
            Check(calls == 3, "1.6.0 engine must submit one INPUT per SendInput call");
            calls = 0;
            Run latest({{0,0,3,3}}, options, nullptr, 3);
            Check(latest.Wait().batchesSubmitted == 1, "latest chord run");
            std::cout << "latest_chord_calls=" << calls << "\n";
            Check(calls == 1, "shared scheduler must submit a key batch in one call");
            MhpDestroyPlan(plan);
        }
        const bool strict = GetEnvironmentVariableW(L"MACROHID_PERF_GATES", nullptr, 0) > 0;
        for (int mode : {1, 2})
        {
            options.precisionMode = mode;
            Run longRun({{ms(300),0,1,1}}, options);
            Sleep(15);
            auto start = QueryCounter();
            Run shortRun({{ms(1),0,1,1}}, options);
            auto stats = shortRun.Wait();
            auto elapsed = ToMicroseconds(QueryCounter() - start, options.qpcFrequency);
            std::cout << "mode=" << mode << " concurrent_short_us=" << elapsed << "\n";
            Check(stats.batchesSubmitted == 1 && elapsed < (strict ? 5000 : 150000), "long plan blocked short plan");
            Run pending({{ms(250),0,1,1}}, options);
            start = QueryCounter(); MhpCancelRun(pending.handle);
            stats = pending.Wait(MhpCancelled);
            elapsed = ToMicroseconds(QueryCounter() - start, options.qpcFrequency);
            std::cout << "cancel_pending_us=" << elapsed << "\n";
            Check(stats.batchesSubmitted == 0 && elapsed < (strict ? 5000 : 150000), "pending cancellation blocked");
            longRun.Wait();
        }
        options.precisionMode = MhpUltraLowJitter;
        {
            calls = 0;
            Run late({{0,0,1,1},{ms(10),1,1,1}}, options, nullptr, 2, QueryCounter() - ms(50), true);
            auto stats = late.Wait();
            Check(ToMicroseconds(firstCalls[1] - firstCalls[0], options.qpcFrequency) >= 10000 - kHoldSlackUs, "late start shortened key hold");
            Check(stats.maxLateUs >= 50000, "timeline reset hid preparation/queue delay");
        }
        {
            calls = 0;
            Run a({{0,0,1,1},{ms(10),1,1,1}}, options, nullptr, 2, 0, true);
            Run b({{0,0,1,1},{ms(15),1,1,1}}, options, nullptr, 2, 0, true);
            Check(a.Wait().batchesSubmitted == 2 && b.Wait().batchesSubmitted == 2 && calls == 4, "same-key plans lost or duplicated actions");
        }
        {
            auto control = MhpCreatePlaybackControl();
            MhpPausePlayback(control);
            Run paused({{ms(1),0,1,1}}, options, control);
            Check(WaitForSingleObject(paused.done, 20) == WAIT_TIMEOUT, "paused run submitted");
            MhpResumePlayback(control); MhpDestroyPlaybackControl(control);
            Check(paused.Wait().batchesSubmitted == 1, "resume/retained control");
        }
        calls = 0; inputsSent = 0;
        {
            // Max (2.1) lock-free race: both workers race every batch; each must be submitted exactly once, also when a second
            // run arrives mid-race (race closes, locked path continues) and when a run is cancelled mid-race.
            MhpRunOptions maxOptions = options; maxOptions.flags = MhpFlagMaxPreemption;
            Check(MhpWarmEngine(&maxOptions, nullptr) == MhpOk, "max warm");
            std::vector<MhpBatch> dense;
            for (int i = 0; i < 2000; i++) dense.push_back({i * options.qpcFrequency / 50000,0,1,1}); // every 20 us
            {
                Run solo(dense, maxOptions);
                const auto stats = solo.Wait();
                std::cout << "max_race batches=" << stats.batchesSubmitted << " helperWins=" << stats.helperWorkerWins
                          << " primaryWins=" << stats.primaryWorkerWins << " maxLateUs=" << stats.maxLateUs << "\n";
                Check(stats.batchesSubmitted == 2000 && calls == 2000 && inputsSent == 2000, "max race lost or duplicated a batch");
            }
            calls = 0; inputsSent = 0;
            {
                Run first(dense, maxOptions);
                Sleep(10);
                Run second(dense, maxOptions);
                Check(first.Wait().batchesSubmitted == 2000 && second.Wait().batchesSubmitted == 2000, "max race with a second run");
                Check(calls == 4000 && inputsSent == 4000, "max race handover duplicated or lost a batch");
            }
            calls = 0;
            {
                Run cancelled(dense, maxOptions);
                Sleep(10);
                MhpCancelRun(cancelled.handle);
                const auto stats = cancelled.Wait(MhpCancelled);
                Check(stats.batchesSubmitted == static_cast<uint32_t>(calls.load()) && stats.batchesSubmitted < 2000, "max race cancel");
            }
            calls = 0; inputsSent = 0;
            {
                // Power guard: display off (or standby) drops Max to 极限 behaviour and restores it after the settle period.
                const DWORD maxClass = GetPriorityClass(GetCurrentProcess());
                g_sharedScheduler.SetPowerSettleMs(100);
                g_sharedScheduler.OnPowerDisplay(true);
                Check(MhpGetPowerHold() == 1, "power hold on display off");
                Check(GetPriorityClass(GetCurrentProcess()) != REALTIME_PRIORITY_CLASS, "realtime class kept during power hold");
                Check(MhpSetHotStandby(1) == MhpOk, "hot standby request during hold");
                Run held(dense, maxOptions);
                const auto stats = held.Wait();
                Check(stats.batchesSubmitted == 2000 && calls == 2000 && inputsSent == 2000, "max run during power hold");
                MhpSetHotStandby(0);
                g_sharedScheduler.OnPowerDisplay(false);
                Check(MhpGetPowerHold() == 1, "power hold released before settle");
                for (int i = 0; i < 100 && MhpGetPowerHold(); i++) Sleep(10);
                Check(MhpGetPowerHold() == 0, "power hold not released after settle");
                Check(GetPriorityClass(GetCurrentProcess()) == maxClass, "process class not restored after power hold");
                g_sharedScheduler.OnPowerSuspend(true);
                Check(MhpGetPowerHold() == 1, "power hold on suspend");
                g_sharedScheduler.SetPowerSettleMs(0);
                g_sharedScheduler.OnPowerSuspend(false);
                Check(MhpGetPowerHold() == 0 && GetPriorityClass(GetCurrentProcess()) == maxClass, "power hold after resume");
                g_sharedScheduler.SetPowerSettleMs(120000);
                std::cout << "max_power_guard ok class=" << maxClass << "\n";
            }
            calls = 0; inputsSent = 0;
            Check(MhpWarmEngine(&options, nullptr) == MhpOk, "back to ultra");
        }
        calls = 0; inputsSent = 0;
        {
            std::vector<MhpBatch> batches;
            for (int i = 0; i < 1000; i++) batches.push_back({i * options.qpcFrequency / 100000,0,1,1});
            std::vector<std::unique_ptr<Run>> stress;
            for (int i = 0; i < 8; i++) stress.push_back(std::make_unique<Run>(batches, options));
            for (auto& run : stress) Check(run->Wait().batchesSubmitted == 1000, "batch ordering/count");
            Check(calls == 8000 && inputsSent == 8000, "duplicate or lost submission");
        }
        {
            partial = 1; calls = 0;
            Run failed({{0,0,2,2},{ms(1),0,2,2}}, options, nullptr, 2);
            auto stats = failed.Wait(MhpWin32Error);
            Check(calls == 1 && stats.nativeInputsSubmitted == 1 && stats.failedSubmissions == 1, "partial submission replay");
            partial = 0;
        }
        {
            blockSubmit = true; insideSubmit = false;
            Run inFlight({{0,0,1,1},{ms(1),0,1,1}}, options);
            auto deadline = QueryCounter() + ms(2000);
            while (!insideSubmit && QueryCounter() < deadline) Sleep(1);
            Check(insideSubmit, "fake API did not start");
            MhpCancelRun(inFlight.handle);
            Check(WaitForSingleObject(inFlight.done, 10) == WAIT_TIMEOUT, "in-flight resource released early");
            blockSubmit = false;
            Check(inFlight.Wait(MhpCancelled).batchesSubmitted == 1, "cancel replayed next batch");
        }
        for (int i = 0; i < 30; i++)
        {
            Run pending({{ms(200),0,1,1}}, options);
            std::thread shutdown([] { MhpShutdownEngine(); });
            pending.Wait(MhpCancelled); shutdown.join();
            Check(MhpWarmEngine(&options, nullptr) == MhpOk, "restart");
        }
        {
            // Tracing is off by default: no records may be kept.
            Run plain({{0,0,1,1}}, options);
            plain.Wait();
            uint32_t written = 99;
            Check(MhpGetBatchTrace(plain.handle, nullptr, 0, &written) == MhpOk && written == 0, "batch trace must be empty when disabled");
        }
        {
            wchar_t self[MAX_PATH]; GetModuleFileNameW(nullptr, self, MAX_PATH);
            std::wstring command = L"\"" + std::wstring(self) + L"\" --batch-trace";
            STARTUPINFOW si{sizeof(si)}; PROCESS_INFORMATION pi{};
            Check(CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi), "launch batch trace mode");
            WaitForSingleObject(pi.hProcess, 30000);
            DWORD code = 1; GetExitCodeProcess(pi.hProcess, &code);
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            Check(code == 0, "batch trace mode failed");
        }
        MhpShutdownEngine();
        std::cout << "PASS: shared scheduler concurrency, pause, cancellation, retention, duplicate prevention, partial failure, shutdown\n";
        return 0;
    }
    catch (const std::exception& ex) { blockSubmit = false; std::cerr << "FAIL: " << ex.what() << "\n"; MhpShutdownEngine(); return 1; }
}
