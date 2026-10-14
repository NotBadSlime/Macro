// Opt-in real SendInput measurement against this process's foreground window.
// Normal regressions and --single-benchmark remain fake-input-only.
struct ReceiverSample
{
    int64_t submitBegin = 0, submitEnd = 0, received = 0;
    UINT sent = 0, deliveries = 0;
};
std::vector<ReceiverSample> receiverSamples;
std::atomic<HWND> receiverWindow{nullptr};
std::atomic<size_t> receiverDelivered{0};
size_t receiverNext = 0;
constexpr ULONG_PTR ReceiverTag = 0x4D48500000000000ULL;

LRESULT CALLBACK ProbeWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_KEYDOWN && wParam == VK_F24)
    {
        const ULONG_PTR tag = static_cast<ULONG_PTR>(GetMessageExtraInfo());
        const auto id = static_cast<uint32_t>(tag);
        if ((tag & 0xFFFFFFFF00000000ULL) == ReceiverTag && id > 0 && id <= receiverSamples.size())
        {
            auto& sample = receiverSamples[id - 1];
            if (sample.deliveries++ == 0)
            {
                sample.received = QueryCounter();
                receiverDelivered.fetch_add(1, std::memory_order_release);
            }
        }
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

class ProbeReceiverWindow
{
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::thread thread;
public:
    ProbeReceiverWindow()
    {
        Check(ready != nullptr, "receiver ready event");
        try
        {
            thread = std::thread([this]
            {
                WNDCLASSW cls{}; cls.lpfnWndProc = ProbeWindowProc;
                cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"MacroHID-SingleReceiver";
                RegisterClassW(&cls);
                HWND window = CreateWindowExW(0, cls.lpszClassName,
                    L"MacroHID single-macro input measurement", WS_OVERLAPPEDWINDOW,
                    CW_USEDEFAULT, CW_USEDEFAULT, 650, 150, nullptr, nullptr, cls.hInstance, nullptr);
                receiverWindow.store(window, std::memory_order_release);
                if (window)
                {
                    CreateWindowExW(0, L"STATIC", L"Click this window if it is not focused. Measurement starts when focused.\nTagged F24 input only; switching windows aborts. Closes automatically.",
                        WS_CHILD | WS_VISIBLE, 16, 16, 600, 65, window, nullptr, cls.hInstance, nullptr);
                    ShowWindow(window, SW_SHOW); SetForegroundWindow(window); SetFocus(window);
                }
                SetEvent(ready);
                if (window)
                {
                    MSG message{};
                    while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message);
                }
                receiverWindow.store(nullptr, std::memory_order_release);
            });
        }
        catch (...) { CloseHandle(ready); ready = nullptr; throw; }
        WaitForSingleObject(ready, INFINITE);
    }
    void Close()
    {
        if (const HWND window = receiverWindow.load(std::memory_order_acquire)) PostMessageW(window, WM_CLOSE, 0, 0);
        if (thread.joinable()) thread.join();
    }
    ~ProbeReceiverWindow()
    {
        Close();
        if (ready) CloseHandle(ready);
    }
};

UINT ReceiverInput(UINT count, INPUT* inputs)
{
    const HWND window = receiverWindow.load(std::memory_order_acquire);
    if (!window || GetForegroundWindow() != window || count != 2 || receiverNext >= receiverSamples.size())
    {
        SetLastError(ERROR_INVALID_WINDOW_HANDLE);
        return 0;
    }
    const size_t index = receiverNext++;
    std::array<INPUT, 2> tagged{inputs[0], inputs[1]};
    for (auto& input : tagged) input.ki.dwExtraInfo = ReceiverTag | (index + 1);
    auto& sample = receiverSamples[index];
    sample.submitBegin = QueryCounter();
    sample.sent = SendInput(count, tagged.data(), sizeof(INPUT));
    sample.submitEnd = QueryCounter();
    if (sample.sent != count && GetForegroundWindow() == window)
    {
        INPUT release = tagged[1];
        SendInput(1, &release, sizeof(INPUT));
    }
    return sample.sent;
}

void RunReceiverBenchmark(MhpRunOptions options, int count, int intervalUs, const char* output)
{
    Check(count > 0 && count <= 1000000 && intervalUs >= 100 && intervalUs <= 100000, "invalid receiver workload");
    Check(options.precisionMode == 1 || options.precisionMode == 2, "invalid receiver mode");
    ProbeTimerResolution timer;
    options.enableCpuScan = options.precisionMode == 2;
    Check(MhpWarmEngine(&options, nullptr) == MhpOk, "receiver warm");
    receiverSamples.assign(count, {}); receiverNext = 0; receiverDelivered = 0;
    ProbeReceiverWindow window;
    const int64_t focusUntil = QueryCounter() + options.qpcFrequency * 5;
    while (receiverWindow && GetForegroundWindow() != receiverWindow.load() && QueryCounter() < focusUntil) Sleep(50);
    Check(receiverWindow && GetForegroundWindow() == receiverWindow.load(), "receiver has no foreground focus");
    std::vector<MhpBatch> batches;
    for (int i = 0; i < count; i++) batches.push_back({i * options.qpcFrequency * intervalUs / 1000000,0,2,2});
    INPUT inputs[2]{};
    for (auto& input : inputs) { input.type = INPUT_KEYBOARD; input.ki.wVk = VK_F24; }
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
    void* plan = nullptr;
    Check(MhpCreatePlan(inputs, 2, batches.data(), count, &plan) == MhpOk, "receiver plan");
    std::unique_ptr<void, decltype(&MhpDestroyPlan)> planOwner(plan, MhpDestroyPlan);
    std::vector<MhpOutlierEvent> trace(count);
    options.outlierThresholdUs = -1; options.outlierEvents = trace.data(); options.outlierEventCapacity = count;
    g_testSubmit = ReceiverInput;
    const int64_t start = QueryCounter();
    void* run = nullptr; HANDLE done = nullptr;
    Check(MhpSubmitPlanAt(plan, &options, nullptr, start, &run, &done) == MhpOk, "receiver submit");
    std::unique_ptr<void, decltype(&MhpReleaseRun)> runOwner(run, MhpReleaseRun);
    Check(WaitForSingleObject(done, static_cast<DWORD>(count * static_cast<int64_t>(intervalUs) / 1000 + 10000)) == WAIT_OBJECT_0, "receiver completion");
    MhpRunStats stats{};
    Check(MhpGetRunResult(run, &stats) == MhpOk && stats.batchesSubmitted == static_cast<uint32_t>(count), "receiver injection failed or focus changed");
    const int64_t until = QueryCounter() + options.qpcFrequency * 2;
    while (receiverDelivered.load(std::memory_order_acquire) != static_cast<size_t>(count) && QueryCounter() < until) Sleep(1);
    Check(receiverDelivered.load(std::memory_order_acquire) == static_cast<size_t>(count), "receiver lost input");
    // Stop message processing before inspecting delivery counters and exporting.
    window.Close();
    std::ofstream file(output);
    Check(file.good(), "receiver output");
    file << "batch,due_qpc,scheduler_qpc,submit_begin_qpc,submit_end_qpc,receiver_qpc,frequency,sent,deliveries\n";
    for (int i = 0; i < count; i++)
    {
        const auto& sample = receiverSamples[i];
        Check(sample.sent == 2 && sample.deliveries == 1, "receiver partial or duplicate input");
        file << i << ',' << trace[i].dueTick << ',' << trace[i].actualTick << ',' << sample.submitBegin << ','
            << sample.submitEnd << ',' << sample.received << ',' << options.qpcFrequency << ',' << sample.sent << ',' << sample.deliveries << '\n';
    }
    std::cout << "real SendInput; single events=" << count << " delivered=" << receiverDelivered
        << " p999_scheduler_us=" << stats.p999LateUs << " max_scheduler_us=" << stats.maxLateUs << "\n";
    g_testSubmit = FakeInput;
}
