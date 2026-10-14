#define MACROHID_PLAYBACK_TEST
#include "../../src/native/MacroHid.NativePlayback/NativePlayback.cpp"
#include <iostream>
#include <stdexcept>
#include <chrono>
#include <fstream>

void Check(bool ok, const char* text) { if (!ok) throw std::runtime_error(text); }
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
UINT FakeInput(UINT count, INPUT*)
{
    int call = calls.fetch_add(1); if (call < 2) firstCalls[call] = QueryCounter();
    insideSubmit = true;
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
                    && firstCalls[1] - firstCalls[0] >= ms(3), "retained single hold shortened");
                MhpReleaseRun(handle);
            }
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
            Check(firstCalls[1] - firstCalls[0] >= ms(10), "late start shortened key hold");
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
        MhpShutdownEngine();
        std::cout << "PASS: shared scheduler concurrency, pause, cancellation, retention, duplicate prevention, partial failure, shutdown\n";
        return 0;
    }
    catch (const std::exception& ex) { blockSubmit = false; std::cerr << "FAIL: " << ex.what() << "\n"; MhpShutdownEngine(); return 1; }
}
