#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "NativePlayback.h"

#include <avrt.h>
#include <mmsystem.h>
#include <intrin.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>
#include <unordered_map>
#include <stdexcept>
#include <condition_variable>
#include <chrono>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif


namespace
{
    constexpr int StandbyWorkerCount = 2;
    constexpr int CoreScanSamplesPerCore = 128;
    constexpr DWORD ProcessPowerThrottlingClass = 4;
    constexpr ULONG ProcessPowerThrottlingCurrentVersion = 1;
    constexpr DWORD ProcessPowerThrottlingExecutionSpeed = 0x00000001;
    constexpr DWORD ProcessPowerThrottlingIgnoreTimerResolution = 0x00000004;

    struct ProcessPowerThrottlingState
    {
        ULONG Version;
        ULONG ControlMask;
        ULONG StateMask;
    };

    struct CoreChoice
    {
        DWORD_PTR mask = 0;
        uint32_t index = std::numeric_limits<uint32_t>::max();
        uint32_t processorNumber = std::numeric_limits<uint32_t>::max();
        int64_t maxLateUs = std::numeric_limits<int64_t>::max();
        int64_t p99LateUs = std::numeric_limits<int64_t>::max();
        int64_t p999LateUs = std::numeric_limits<int64_t>::max();
        int64_t meanLateUs = std::numeric_limits<int64_t>::max();
        int64_t score = std::numeric_limits<int64_t>::max();
        uint32_t physicalCoreId = std::numeric_limits<uint32_t>::max();
        uint32_t efficiencyClass = 0;
    };

    struct ModifierChordEdges
    {
        int downs = 0;
        int ups = 0;
        bool otherKeyDown = false;
        bool keyUp = false;
    };

    struct NativePlan
    {
        struct Scratch { std::vector<int64_t> late, loopLate, submitted; bool available = true; };
        std::mutex scratchGate;
        std::array<Scratch, 2> scratch;
        std::atomic<int> references{1};
        std::array<CoreChoice, 2> configuredCores{};
        std::vector<INPUT> inputs;
        std::vector<MhpBatch> batches;
        std::vector<ModifierChordEdges> chordEdges;
        std::vector<std::vector<std::pair<size_t, int64_t>>> releaseDependencies;
    };

    void CompileReleaseDependencies(NativePlan& plan)
    {
        for (auto& slot : plan.scratch)
        {
            slot.late.reserve(plan.batches.size());
            slot.loopLate.reserve(plan.batches.size());
            slot.submitted.resize(plan.batches.size());
        }
        std::unordered_map<uint64_t, size_t> held;
        plan.releaseDependencies.resize(plan.batches.size());
        for (size_t index = 0; index < plan.batches.size(); index++)
        {
            const auto& batch = plan.batches[index];
            auto transition = [&](uint64_t key, bool up)
            {
                auto found = held.find(key);
                if (up)
                {
                    if (found != held.end())
                    {
                        const auto down = found->second;
                        if (down != index) plan.releaseDependencies[index].push_back({down, std::max<int64_t>(0, batch.dueTicks - plan.batches[down].dueTicks)});
                        held.erase(found);
                    }
                }
                else if (found == held.end()) held.emplace(key, index);
            };
            for (uint32_t i = 0; i < batch.inputCount; i++)
            {
                const auto& input = plan.inputs[batch.inputOffset + i];
                if (input.type == INPUT_KEYBOARD)
                    transition((static_cast<uint64_t>(input.ki.wVk) << 32) | (static_cast<uint64_t>(input.ki.wScan) << 8) | (input.ki.dwFlags & ~KEYEVENTF_KEYUP), (input.ki.dwFlags & KEYEVENTF_KEYUP) != 0);
                if (input.type == INPUT_MOUSE)
                {
                    const DWORD downs[]{MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_XDOWN};
                    const DWORD ups[]{MOUSEEVENTF_LEFTUP, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_XUP};
                    for (int button = 0; button < 4; button++)
                    {
                        uint64_t key = (1ULL << 63) | button | (button == 3 ? static_cast<uint64_t>(input.mi.mouseData) << 8 : 0);
                        if (input.mi.dwFlags & downs[button]) transition(key, false);
                        if (input.mi.dwFlags & ups[button]) transition(key, true);
                    }
                }
            }
        }
    }

    thread_local std::wstring g_lastError;

    using NtSetTimerResolutionFn = LONG(NTAPI*)(ULONG, BOOLEAN, PULONG);

    void SetLastErrorText(const wchar_t* text)
    {
        g_lastError = text == nullptr ? L"" : text;
    }

    void SetLastWin32ErrorText(const wchar_t* prefix, DWORD error)
    {
        wchar_t buffer[512]{};
        swprintf_s(buffer, L"%s win32=%lu", prefix, static_cast<unsigned long>(error));
        g_lastError = buffer;
    }

    int64_t QueryCounter()
    {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
    }

    int64_t QueryFrequency()
    {
        LARGE_INTEGER value{};
        QueryPerformanceFrequency(&value);
        return value.QuadPart;
    }

    int64_t ToMicroseconds(int64_t ticks, int64_t frequency)
    {
        if (frequency <= 0)
        {
            return 0;
        }

        const double scaled = static_cast<double>(ticks) * 1000000.0 / static_cast<double>(frequency);
        return static_cast<int64_t>(std::llround(scaled));
    }

    constexpr int64_t ModifierChordHoldMs = 30;

    int64_t ModifierChordHoldTicks(int64_t frequency)
    {
        if (frequency <= 0)
        {
            return 1;
        }

        return std::max<int64_t>(1, frequency * ModifierChordHoldMs / 1000);
    }

    bool IsModifierVirtualKey(WORD virtualKey)
    {
        switch (virtualKey)
        {
        case 0x10:
        case 0x11:
        case 0x12:
        case 0xA0:
        case 0xA1:
        case 0xA2:
        case 0xA3:
        case 0xA4:
        case 0xA5:
        case 0x5B:
        case 0x5C:
            return true;
        default:
            return false;
        }
    }

    ModifierChordEdges InspectModifierChord(const INPUT* inputs, uint32_t count)
    {
        ModifierChordEdges edges;
        if (inputs == nullptr)
        {
            return edges;
        }

        for (uint32_t i = 0; i < count; i++)
        {
            if (inputs[i].type != INPUT_KEYBOARD)
            {
                continue;
            }

            const auto& key = inputs[i].ki;
            if ((key.dwFlags & KEYEVENTF_UNICODE) != 0 || key.wVk == 0)
            {
                continue;
            }

            const bool up = (key.dwFlags & KEYEVENTF_KEYUP) != 0;
            if (IsModifierVirtualKey(key.wVk))
            {
                if (up)
                {
                    edges.ups++;
                }
                else
                {
                    edges.downs++;
                }
            }
            else if (!up)
            {
                edges.otherKeyDown = true;
            }

            if (up)
            {
                edges.keyUp = true;
            }
        }

        return edges;
    }

    struct ModifierChordClock
    {
        std::atomic<int> held{ 0 };
        std::atomic<int64_t> anchor{ 0 };

        int64_t AdjustDue(int64_t dueTick, const ModifierChordEdges& edges, int64_t holdTicks) const
        {
            const int heldNow = held.load(std::memory_order_acquire);
            const int64_t anchorNow = anchor.load(std::memory_order_acquire);
            if (heldNow <= 0 || anchorNow <= 0 || !edges.keyUp)
            {
                return dueTick;
            }

            const int64_t earliest = anchorNow + holdTicks;
            return dueTick < earliest ? earliest : dueTick;
        }

        void NoteSent(const ModifierChordEdges& edges, int64_t actualTick)
        {
            int heldNow = held.load(std::memory_order_relaxed);
            if (edges.downs > 0)
            {
                heldNow += edges.downs;
                anchor.store(actualTick, std::memory_order_relaxed);
            }

            if (edges.otherKeyDown && heldNow > 0)
            {
                anchor.store(actualTick, std::memory_order_relaxed);
            }

            heldNow -= edges.ups;
            if (heldNow <= 0)
            {
                heldNow = 0;
                anchor.store(0, std::memory_order_relaxed);
            }

            held.store(heldNow, std::memory_order_release);
        }
    };

    void ExtendDueForModifierChord(
        int64_t& baseDueTick,
        int64_t& dueTick,
        const ModifierChordEdges& edges,
        const ModifierChordClock& clock,
        int64_t holdTicks,
        int64_t pausedTicks)
    {
        const int64_t chordDue = clock.AdjustDue(dueTick, edges, holdTicks);
        if (chordDue <= dueTick)
        {
            return;
        }

        dueTick = chordDue;
        baseDueTick = chordDue - pausedTicks;
    }

    bool IsCancelled(volatile long* cancelFlag)
    {
        return cancelFlag != nullptr && *cancelFlag != 0;
    }

    bool IsRelativeMouseInput(const INPUT& input)
    {
        return input.type == INPUT_MOUSE
            && (input.mi.dwFlags & MOUSEEVENTF_MOVE) != 0
            && (input.mi.dwFlags & MOUSEEVENTF_ABSOLUTE) == 0;
    }

    void RecenterCursorIfNeeded(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        POINT point{};
        if (!GetCursorPos(&point))
        {
            return;
        }

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        RECT clip{};
        if (GetClipCursor(&clip))
        {
            const int clipWidth = clip.right - clip.left;
            const int clipHeight = clip.bottom - clip.top;
            if (clipWidth > 1 && clipHeight > 1)
            {
                left = clip.left;
                top = clip.top;
                width = clipWidth;
                height = clipHeight;
            }
        }

        if (width <= 1 || height <= 1)
        {
            return;
        }

        constexpr int margin = 16;
        const int nextX = point.x + dx;
        const int nextY = point.y + dy;
        if (nextX >= left + margin
            && nextX <= left + width - 1 - margin
            && nextY >= top + margin
            && nextY <= top + height - 1 - margin)
        {
            return;
        }

        ClipCursor(nullptr);
        SetCursorPos(left + width / 2, top + height / 2);
    }

    #ifdef MACROHID_PLAYBACK_TEST
    UINT (*g_testSubmit)(UINT, INPUT*) = nullptr;
    #endif
    UINT SubmitPlaybackInput(UINT count, INPUT* inputs)
    {
        #ifdef MACROHID_PLAYBACK_TEST
        return g_testSubmit(count, inputs);
        #endif
        if (count == 0 || inputs == nullptr)
        {
            return 0;
        }

        if (std::none_of(inputs, inputs + count, IsRelativeMouseInput))
            return SendInput(count, inputs, sizeof(INPUT));

        UINT sent = 0;
        for (UINT i = 0; i < count; i++)
        {
            if (IsRelativeMouseInput(inputs[i]))
            {
                RecenterCursorIfNeeded(inputs[i].mi.dx, inputs[i].mi.dy);
            }

            const UINT one = SendInput(1, inputs + i, sizeof(INPUT));
            sent += one;
            if (one != 1)
            {
                break;
            }
        }

        return sent;
    }

    struct PlaybackControl
    {
        std::atomic<int> references{1};
        volatile long pauseCount = 0;
        volatile long pauseFlag = 0;
        volatile long long pauseStartedTick = 0;
        volatile long long totalPausedTicks = 0;
    };

    bool IsPaused(const PlaybackControl* control)
    {
        return control != nullptr && control->pauseFlag != 0;
    }

    int64_t TotalPausedTicks(const PlaybackControl* control)
    {
        if (control == nullptr)
        {
            return 0;
        }

        return InterlockedCompareExchange64(
            const_cast<volatile long long*>(&control->totalPausedTicks),
            0,
            0);
    }

    bool WaitForResume(PlaybackControl* control, volatile long* cancelFlag)
    {
        while (IsPaused(control))
        {
            if (IsCancelled(cancelFlag))
            {
                return false;
            }

            long pausedValue = 1;
            WaitOnAddress(
                const_cast<volatile long*>(&control->pauseFlag),
                &pausedValue,
                sizeof(pausedValue),
                10);
        }

        return !IsCancelled(cancelFlag);
    }

    bool SpinUntilControlled(
        int64_t baseDueTick,
        int64_t rescueDelayTicks,
        PlaybackControl* control,
        volatile long* cancelFlag,
        int64_t& effectiveDueTick)
    {
        while (!IsCancelled(cancelFlag))
        {
            if (!WaitForResume(control, cancelFlag))
            {
                return false;
            }

            const int64_t pausedTicks = TotalPausedTicks(control);
            effectiveDueTick = baseDueTick + pausedTicks;
            const int64_t targetTick = effectiveDueTick + rescueDelayTicks;
            const int64_t now = QueryCounter();
            if (now >= targetTick)
            {
                if (!IsPaused(control))
                {
                    return !IsCancelled(cancelFlag);
                }

                continue;
            }

            const int64_t remainingUs = ToMicroseconds(targetTick - now, QueryFrequency());
            if (remainingUs > 2500)
            {
                Sleep(remainingUs > 30'000 ? 1 : 0);
                continue;
            }

            while (!IsCancelled(cancelFlag)
                && !IsPaused(control)
                && QueryCounter() < targetTick)
            {
                _mm_pause();
            }

            if (!IsPaused(control))
            {
                return !IsCancelled(cancelFlag);
            }
        }

        return false;
    }

    DWORD CurrentProcessorNumber()
    {
        PROCESSOR_NUMBER processor{};
        GetCurrentProcessorNumberEx(&processor);
        return processor.Group * 64u + processor.Number;
    }

    void PauseProcessor()
    {
        _mm_pause();
    }

    class WaitableTimer
    {
    public:
        WaitableTimer()
            : handle(CreateWaitableTimerExW(
                nullptr,
                nullptr,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_ALL_ACCESS))
        {
        }

        ~WaitableTimer()
        {
            if (handle != nullptr)
            {
                CloseHandle(handle);
            }
        }

        bool WaitForMicroseconds(int64_t waitUs, volatile long* cancelFlag = nullptr)
        {
            if (handle == nullptr || waitUs <= 0)
            {
                return false;
            }

            LARGE_INTEGER due{};
            constexpr int64_t maxWaitUs = (std::numeric_limits<int64_t>::max() / 10) - 1;
            const int64_t clampedUs = std::min(waitUs, maxWaitUs);
            due.QuadPart = -std::max<int64_t>(1, clampedUs * 10);
            if (!SetWaitableTimerEx(handle, &due, 0, nullptr, nullptr, nullptr, 0))
            {
                return false;
            }

            while (!IsCancelled(cancelFlag))
            {
                const DWORD result = WaitForSingleObject(handle, 250);
                if (result == WAIT_OBJECT_0)
                {
                    return true;
                }

                if (result != WAIT_TIMEOUT)
                {
                    return false;
                }
            }

            return false;
        }

    private:
        HANDLE handle;
    };

    std::atomic<int> g_playbackActive{ 0 };

    struct PlaybackActivityScope
    {
        PlaybackActivityScope()
        {
            g_playbackActive.fetch_add(1, std::memory_order_acq_rel);
        }

        ~PlaybackActivityScope()
        {
            g_playbackActive.fetch_sub(1, std::memory_order_acq_rel);
        }
    };

    bool PlaybackIsActive()
    {
        return g_playbackActive.load(std::memory_order_acquire) != 0;
    }

    struct RedundantWaitState
    {
        std::atomic<uint32_t> generation{ 0 };
        std::atomic<int> claimed{ 0 };
        std::atomic<int> completed{ 0 };
        std::atomic<int> stop{ 0 };
        const NativePlan* plan = nullptr;
        const MhpBatch* batch = nullptr;
        int64_t dueTick = 0;
        int64_t helperRescueDelayTicks = 0;
        int64_t frequency = 0;
        volatile long* cancelFlag = nullptr;
        int64_t actualTick = 0;
        DWORD cpu = 0;
        int64_t submitDurationUs = 0;
        UINT sent = 0;
        DWORD win32Error = 0;
        uint32_t nativeInputsSubmitted = 0;
        bool helperWon = false;
    };

    void SpinUntil(int64_t dueTick, volatile long* cancelFlag)
    {
        while (!IsCancelled(cancelFlag) && QueryCounter() < dueTick)
        {
            PauseProcessor();
        }
    }

    uint32_t ProcessorNumberFromMask(DWORD_PTR targetMask)
    {
        if (targetMask == 0)
        {
            return std::numeric_limits<uint32_t>::max();
        }

        for (uint32_t bit = 0; bit < sizeof(DWORD_PTR) * 8; bit++)
        {
            const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
            if ((targetMask & mask) != 0)
            {
                return bit;
            }
        }

        return std::numeric_limits<uint32_t>::max();
    }

    bool ResolveCpuSetIdForLogicalProcessor(uint32_t logicalProcessor, ULONG& cpuSetId)
    {
        ULONG returnedLength = 0;
        if (GetSystemCpuSetInformation(nullptr, 0, &returnedLength, GetCurrentProcess(), 0)
            || returnedLength == 0)
        {
            return false;
        }

        std::vector<std::byte> buffer(returnedLength);
        auto* information = reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buffer.data());
        if (!GetSystemCpuSetInformation(information, returnedLength, &returnedLength, GetCurrentProcess(), 0))
        {
            return false;
        }

        size_t offset = 0;
        while (offset + sizeof(SYSTEM_CPU_SET_INFORMATION) <= buffer.size())
        {
            auto* item = reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buffer.data() + offset);
            if (item->Size == 0 || offset + item->Size > buffer.size())
            {
                break;
            }

            if (item->Type == CpuSetInformation
                && item->CpuSet.Group == 0
                && item->CpuSet.LogicalProcessorIndex == logicalProcessor)
            {
                cpuSetId = item->CpuSet.Id;
                return true;
            }

            offset += item->Size;
        }

        return false;
    }

    bool ApplyThreadCpuSetForMask(HANDLE thread, DWORD_PTR affinityMask, uint32_t* cpuSetAppliedCounter)
    {
        const uint32_t logicalProcessor = ProcessorNumberFromMask(affinityMask);
        if (logicalProcessor == std::numeric_limits<uint32_t>::max())
        {
            return false;
        }

        ULONG cpuSetId = 0;
        if (!ResolveCpuSetIdForLogicalProcessor(logicalProcessor, cpuSetId))
        {
            return false;
        }

        if (!SetThreadSelectedCpuSets(thread, &cpuSetId, 1))
        {
            return false;
        }

        if (cpuSetAppliedCounter != nullptr)
        {
            (*cpuSetAppliedCounter)++;
        }

        return true;
    }

    int64_t PercentileOfSorted(const std::vector<int64_t>& sorted, double percentile)
    {
        if (sorted.empty())
        {
            return 0;
        }

        const auto clamped = std::clamp(percentile, 0.0, 1.0);
        const auto itemIndex = static_cast<size_t>(
            std::floor(clamped * static_cast<double>(sorted.size() - 1)));
        return sorted[itemIndex];
    }

    uint32_t CountAllowedProcessors(DWORD_PTR processMask)
    {
        uint32_t count = 0;
        for (uint32_t bit = 0; bit < sizeof(DWORD_PTR) * 8; bit++)
        {
            const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
            if ((processMask & mask) != 0)
            {
                count++;
            }
        }

        return count;
    }

    int64_t CoreChoiceScore(
        int64_t maxLateUs,
        int64_t p999LateUs,
        int64_t p99LateUs,
        int64_t meanLateUs,
        uint32_t logicalIndex,
        uint32_t allowedProcessorCount)
    {
        const auto frontQuarterPenalty = allowedProcessorCount > 8 && logicalIndex < allowedProcessorCount / 4u
            ? 50
            : 0;
        return (maxLateUs * 1000)
            + (p999LateUs * 100)
            + (p99LateUs * 10)
            + meanLateUs
            + frontQuarterPenalty;
    }

    bool CoreChoiceBetter(const CoreChoice& candidate, const CoreChoice& best)
    {
        if (candidate.mask == 0)
        {
            return false;
        }

        if (best.mask == 0)
        {
            return true;
        }

        if (candidate.score != best.score)
        {
            return candidate.score < best.score;
        }

        if (candidate.maxLateUs != best.maxLateUs)
        {
            return candidate.maxLateUs < best.maxLateUs;
        }

        return candidate.processorNumber > best.processorNumber;
    }

    struct LogicalCoreTopology
    {
        uint32_t physicalCoreId = 0;
        uint32_t efficiencyClass = 0;
    };

    std::vector<LogicalCoreTopology> QueryLogicalCoreTopology()
    {
        const auto processorSlots = static_cast<size_t>(sizeof(DWORD_PTR) * 8);
        std::vector<LogicalCoreTopology> topology(processorSlots);
        for (uint32_t bit = 0; bit < processorSlots; bit++)
        {
            topology[bit].physicalCoreId = bit;
        }

        DWORD length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &length);
        if (length == 0)
        {
            return topology;
        }

        std::vector<unsigned char> buffer(length);
        auto* info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data());
        if (!GetLogicalProcessorInformationEx(RelationProcessorCore, info, &length))
        {
            return topology;
        }

        uint32_t physicalCoreId = 0;
        DWORD offset = 0;
        while (offset + sizeof(DWORD) * 2 < length)
        {
            auto* current = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data() + offset);
            if (current->Size == 0 || offset + current->Size > length)
            {
                break;
            }

            if (current->Relationship == RelationProcessorCore)
            {
                const auto& core = current->Processor;
                for (WORD groupIndex = 0; groupIndex < core.GroupCount; groupIndex++)
                {
                    const auto& group = core.GroupMask[groupIndex];
                    if (group.Group != 0)
                    {
                        continue;
                    }

                    for (uint32_t bit = 0; bit < processorSlots; bit++)
                    {
                        const KAFFINITY bitMask = static_cast<KAFFINITY>(1) << bit;
                        if ((group.Mask & bitMask) == 0)
                        {
                            continue;
                        }

                        topology[bit].physicalCoreId = physicalCoreId;
                        topology[bit].efficiencyClass = core.EfficiencyClass;
                    }
                }

                physicalCoreId++;
            }

            offset += current->Size;
        }

        return topology;
    }

    LogicalCoreTopology LookupLogicalCore(uint32_t processorNumber)
    {
        static std::vector<LogicalCoreTopology> topology = QueryLogicalCoreTopology();
        if (processorNumber >= topology.size())
        {
            return LogicalCoreTopology{ processorNumber, 0 };
        }

        return topology[processorNumber];
    }

    bool g_cpuScanCacheValid = false;
    bool g_cpuScanCacheMeasuredWithJitter = false;

    bool CpuScanCacheReady()
    {
        return g_cpuScanCacheValid && g_cpuScanCacheMeasuredWithJitter;
    }

    CoreChoice MeasureCandidateCore(
        HANDLE thread,
        DWORD_PTR mask,
        uint32_t index,
        uint32_t allowedProcessorCount,
        int64_t frequency,
        int sampleCount,
        uint32_t physicalCoreId,
        uint32_t efficiencyClass)
    {
        CoreChoice choice{ mask, index, ProcessorNumberFromMask(mask), std::numeric_limits<int64_t>::max() };
        choice.physicalCoreId = physicalCoreId;
        choice.efficiencyClass = efficiencyClass;
        const DWORD_PTR previous = SetThreadAffinityMask(thread, mask);
        if (previous == 0)
        {
            return choice;
        }

        const int64_t intervalTicks = std::max<int64_t>(1, frequency / 1000);
        int64_t dueTick = QueryCounter() + intervalTicks;
        int64_t maxLate = 0;
        int64_t sumLate = 0;
        std::vector<int64_t> lateSamples;
        const int samples = std::max(1, sampleCount);
        lateSamples.reserve(static_cast<size_t>(samples));
        for (int i = 0; i < samples; i++)
        {
            SpinUntil(dueTick, nullptr);
            const int64_t late = std::max<int64_t>(0, ToMicroseconds(QueryCounter() - dueTick, frequency));
            maxLate = std::max(maxLate, late);
            sumLate += late;
            lateSamples.push_back(late);
            dueTick += intervalTicks;
        }

        SetThreadAffinityMask(thread, previous);
        std::sort(lateSamples.begin(), lateSamples.end());
        choice.maxLateUs = maxLate;
        choice.p99LateUs = PercentileOfSorted(lateSamples, 0.99);
        choice.p999LateUs = PercentileOfSorted(lateSamples, 0.999);
        choice.meanLateUs = sumLate / std::max<int64_t>(1, static_cast<int64_t>(lateSamples.size()));
        choice.score = CoreChoiceScore(
            choice.maxLateUs,
            choice.p999LateUs,
            choice.p99LateUs,
            choice.meanLateUs,
            choice.index,
            allowedProcessorCount);
        return choice;
    }

    std::vector<CoreChoice> ScanEligibleCores(bool enableCpuScan, MhpCoreProgressFn progress = nullptr, bool force = false, DWORD_PTR allowedMask = 0)
    {
        static std::mutex cacheMutex;
        static std::vector<CoreChoice> cachedChoices;
        static DWORD_PTR cachedProcessMask = 0;
        static bool cacheValid = false;
        static bool cacheMeasuredWithJitter = false;

        HANDLE process = GetCurrentProcess();
        HANDLE thread = GetCurrentThread();
        DWORD_PTR processMask = 0;
        DWORD_PTR systemMask = 0;
        if (!GetProcessAffinityMask(process, &processMask, &systemMask) || processMask == 0)
        {
            return {};
        }

        if (allowedMask) processMask &= allowedMask;
        if (!processMask) return {};
        std::vector<CoreChoice> cachedSnapshot;
        bool useCache = false;
        {
            std::lock_guard<std::mutex> guard(cacheMutex);
            if (!force
                && cacheValid
                && cachedProcessMask == processMask
                && (!enableCpuScan || cacheMeasuredWithJitter || PlaybackIsActive()))
            {
                cachedSnapshot = cachedChoices;
                useCache = true;
            }
        }

        if (useCache)
        {
            if (progress != nullptr && enableCpuScan && !PlaybackIsActive())
            {
                const int total = static_cast<int>(cachedSnapshot.size());
                for (int index = 0; index < total; index++)
                {
                    progress(
                        cachedSnapshot[static_cast<size_t>(index)].processorNumber,
                        index + 1,
                        total,
                        cachedSnapshot[static_cast<size_t>(index)].maxLateUs);
                }
            }

            return cachedSnapshot;
        }

        if (enableCpuScan && PlaybackIsActive())
        {
            return {};
        }

        const int64_t frequency = QueryFrequency();
        std::vector<CoreChoice> choices;
        choices.reserve(CountAllowedProcessors(processMask));
        uint32_t logicalIndex = 0;
        const uint32_t allowedProcessorCount = CountAllowedProcessors(processMask);
        for (uint32_t bit = 0; bit < sizeof(DWORD_PTR) * 8; bit++)
        {
            const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
            if ((processMask & mask) == 0)
            {
                continue;
            }

            const auto topology = LookupLogicalCore(bit);
            if (progress != nullptr && enableCpuScan)
            {
                progress(bit, static_cast<int32_t>(logicalIndex + 1), static_cast<int32_t>(allowedProcessorCount), -1);
            }

            const auto candidate = enableCpuScan
                ? MeasureCandidateCore(
                    thread,
                    mask,
                    logicalIndex,
                    allowedProcessorCount,
                    frequency,
                    CoreScanSamplesPerCore,
                    topology.physicalCoreId,
                    topology.efficiencyClass)
                : CoreChoice{ mask, logicalIndex, ProcessorNumberFromMask(mask), 0, 0, 0, 0, static_cast<int64_t>(logicalIndex), topology.physicalCoreId, topology.efficiencyClass };
            if (candidate.mask != 0)
            {
                choices.push_back(candidate);
                if (progress != nullptr && enableCpuScan)
                {
                    progress(
                        candidate.processorNumber,
                        static_cast<int32_t>(logicalIndex + 1),
                        static_cast<int32_t>(allowedProcessorCount),
                        candidate.maxLateUs);
                }
            }

            logicalIndex++;
        }

        std::sort(
            choices.begin(),
            choices.end(),
            [](const CoreChoice& left, const CoreChoice& right)
            {
                if (left.efficiencyClass != right.efficiencyClass)
                {
                    return left.efficiencyClass < right.efficiencyClass;
                }

                return CoreChoiceBetter(left, right);
            });

        {
            std::lock_guard<std::mutex> guard(cacheMutex);
            cachedChoices = choices;
            cachedProcessMask = processMask;
            cacheValid = !choices.empty();
            cacheMeasuredWithJitter = enableCpuScan;
            g_cpuScanCacheValid = cacheValid;
            g_cpuScanCacheMeasuredWithJitter = cacheMeasuredWithJitter;
        }

        return choices;
    }

    CoreChoice SelectLowestJitterCore(bool enableCpuScan)
    {
        const auto choices = ScanEligibleCores(enableCpuScan);
        return choices.empty() ? CoreChoice{} : choices.front();
    }

    std::vector<CoreChoice> SelectLowestJitterCores(int requestedCount, bool enableCpuScan, DWORD_PTR allowedMask = 0)
    {
        std::vector<CoreChoice> selected;
        if (requestedCount <= 0)
        {
            return selected;
        }

        const auto choices = ScanEligibleCores(enableCpuScan, nullptr, false, allowedMask);
        selected.reserve(std::min<int>(requestedCount, static_cast<int>(choices.size())));
        std::vector<uint32_t> usedPhysicalCores;
        for (const auto& choice : choices)
        {
            if (choice.processorNumber == 0 && static_cast<int>(choices.size()) > requestedCount)
            {
                continue;
            }

            if (std::find(usedPhysicalCores.begin(), usedPhysicalCores.end(), choice.physicalCoreId) != usedPhysicalCores.end())
            {
                continue;
            }

            usedPhysicalCores.push_back(choice.physicalCoreId);
            selected.push_back(choice);
            if (static_cast<int>(selected.size()) >= requestedCount)
            {
                break;
            }
        }

        if (static_cast<int>(selected.size()) < requestedCount)
        {
            for (const auto& choice : choices)
            {
                if (std::any_of(selected.begin(), selected.end(), [&](const CoreChoice& existing) { return existing.mask == choice.mask; }))
                {
                    continue;
                }

                selected.push_back(choice);
                if (static_cast<int>(selected.size()) >= requestedCount)
                {
                    break;
                }
            }
        }

        return selected;
    }

    std::vector<DWORD_PTR> SelectHelperCoreMasks(DWORD_PTR avoidMask, int requestedCount)
    {
        std::vector<DWORD_PTR> masks;
        if (requestedCount <= 0)
        {
            return masks;
        }

        DWORD_PTR processMask = 0;
        DWORD_PTR systemMask = 0;
        if (!GetProcessAffinityMask(GetCurrentProcess(), &processMask, &systemMask) || processMask == 0)
        {
            return masks;
        }

        const auto bitCount = static_cast<uint32_t>(sizeof(DWORD_PTR) * 8);
        const uint32_t current = CurrentProcessorNumber() % bitCount;
        for (uint32_t offset = 1; offset < bitCount; offset++)
        {
            const uint32_t bit = (current + offset) % bitCount;
            const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
            if ((processMask & mask) != 0 && (avoidMask == 0 || (mask & avoidMask) == 0))
            {
                masks.push_back(mask);
                if (static_cast<int>(masks.size()) >= requestedCount)
                {
                    break;
                }
            }
        }

        return masks;
    }

    DWORD_PTR SelectHelperCoreMask(DWORD_PTR avoidMask)
    {
        auto masks = SelectHelperCoreMasks(avoidMask, 1);
        return masks.empty() ? 0 : masks[0];
    }

    uint32_t MaskToLogicalIndex(DWORD_PTR targetMask)
    {
        DWORD_PTR processMask = 0;
        DWORD_PTR systemMask = 0;
        if (targetMask == 0
            || !GetProcessAffinityMask(GetCurrentProcess(), &processMask, &systemMask))
        {
            return std::numeric_limits<uint32_t>::max();
        }

        uint32_t logicalIndex = 0;
        for (uint32_t bit = 0; bit < sizeof(DWORD_PTR) * 8; bit++)
        {
            const DWORD_PTR mask = static_cast<DWORD_PTR>(1) << bit;
            if ((processMask & mask) == 0)
            {
                continue;
            }

            if ((targetMask & mask) != 0)
            {
                return logicalIndex;
            }

            logicalIndex++;
        }

        return std::numeric_limits<uint32_t>::max();
    }

    bool ShouldUseRedundantWaiter(
        const MhpRunOptions& options,
        int64_t previousDueTick,
        int64_t startTick,
        int64_t dueTick,
        int64_t frequency)
    {
        if (options.precisionMode != MhpUltraLowJitter || frequency <= 0)
        {
            return false;
        }

        const int64_t intervalTicks = previousDueTick > 0 ? dueTick - previousDueTick : dueTick - startTick;
        const int64_t intervalUs = ToMicroseconds(std::max<int64_t>(0, intervalTicks), frequency);
        return intervalUs > 0 && intervalUs <= 2500;
    }

    bool ShouldUseSingleOwnerFastLane(
        const NativePlan& plan,
        const MhpRunOptions& options,
        int64_t frequency)
    {
        if (options.precisionMode != MhpUltraLowJitter
            || options.loopStepCount == 0
            || frequency <= 0
            || plan.batches.size() < 2)
        {
            return false;
        }

        const size_t requestedSampleCount = static_cast<size_t>(options.loopStepCount) * 2;
        const size_t sampleCount = std::min(
            plan.batches.size(),
            std::max<size_t>(2, requestedSampleCount));
        int64_t previousDueTicks = plan.batches[0].dueTicks;
        for (size_t i = 1; i < sampleCount; i++)
        {
            const int64_t intervalTicks = plan.batches[i].dueTicks - previousDueTicks;
            previousDueTicks = plan.batches[i].dueTicks;
            const int64_t intervalUs = ToMicroseconds(std::max<int64_t>(0, intervalTicks), frequency);
            if (intervalUs <= 0 || intervalUs > 2500)
            {
                return false;
            }
        }

        return true;
    }

    int64_t HelperRescueDelayTicks(int64_t frequency)
    {
        return std::max<int64_t>(1, frequency * 2 / 1000000);
    }

    void AttemptRedundantSubmit(RedundantWaitState& state, bool helper)
    {
        const int64_t targetTick = helper ? state.dueTick + state.helperRescueDelayTicks : state.dueTick;
        while (!IsCancelled(state.cancelFlag)
            && state.claimed.load(std::memory_order_acquire) == 0
            && QueryCounter() < targetTick)
        {
            PauseProcessor();
        }

        if (IsCancelled(state.cancelFlag))
        {
            return;
        }

        int expected = 0;
        if (!state.claimed.compare_exchange_strong(expected, 1, std::memory_order_acq_rel))
        {
            return;
        }

        const auto* batch = state.batch;
        const int64_t actualTick = QueryCounter();
        const DWORD cpu = CurrentProcessorNumber();
        int64_t submitDurationUs = 0;
        UINT sent = batch == nullptr ? 0 : batch->inputCount;
        DWORD win32Error = 0;
        uint32_t nativeInputsSubmitted = 0;

        if (batch != nullptr && batch->inputCount > 0)
        {
            auto* inputPointer = const_cast<INPUT*>(state.plan->inputs.data() + batch->inputOffset);
            const int64_t submitStart = QueryCounter();
            sent = SubmitPlaybackInput(batch->inputCount, inputPointer);
            const int64_t submitEnd = QueryCounter();
            submitDurationUs = ToMicroseconds(submitEnd - submitStart, state.frequency);
            if (sent != batch->inputCount)
            {
                win32Error = GetLastError();
            }
            else
            {
                nativeInputsSubmitted = batch->inputCount;
            }
        }

        state.actualTick = actualTick;
        state.cpu = cpu;
        state.submitDurationUs = submitDurationUs;
        state.sent = sent;
        state.win32Error = win32Error;
        state.nativeInputsSubmitted = nativeInputsSubmitted;
        state.helperWon = helper;
        state.completed.store(1, std::memory_order_release);
    }

    void RunRedundantWaiter(RedundantWaitState* state, DWORD_PTR helperMask)
    {
        HANDLE thread = GetCurrentThread();
        DWORD_PTR oldAffinity = 0;
        DWORD oldIdealProcessor = MAXIMUM_PROCESSORS;
        HANDLE avrtHandle = nullptr;
        DWORD avrtTaskIndex = 0;
        bool cpuSetApplied = false;
        BOOL previousThreadPriorityBoostDisabled = FALSE;
        const bool threadPriorityBoostCaptured = GetThreadPriorityBoost(thread, &previousThreadPriorityBoostDisabled) != FALSE;
        if (threadPriorityBoostCaptured)
        {
            SetThreadPriorityBoost(thread, TRUE);
        }

        if (helperMask != 0)
        {
            oldAffinity = SetThreadAffinityMask(thread, helperMask);
            const uint32_t processorNumber = ProcessorNumberFromMask(helperMask);
            oldIdealProcessor = SetThreadIdealProcessor(thread, processorNumber);
            uint32_t ignored = 0;
            cpuSetApplied = ApplyThreadCpuSetForMask(thread, helperMask, &ignored);
        }

        SetThreadPriority(thread, THREAD_PRIORITY_TIME_CRITICAL);
        avrtHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &avrtTaskIndex);
        if (avrtHandle != nullptr)
        {
            AvSetMmThreadPriority(avrtHandle, AVRT_PRIORITY_CRITICAL);
        }

        uint32_t seenGeneration = 0;
        while (state->stop.load(std::memory_order_acquire) == 0)
        {
            const uint32_t generation = state->generation.load(std::memory_order_acquire);
            if (generation == seenGeneration)
            {
                PauseProcessor();
                continue;
            }

            seenGeneration = generation;
            AttemptRedundantSubmit(*state, true);
        }

        if (avrtHandle != nullptr)
        {
            AvRevertMmThreadCharacteristics(avrtHandle);
        }

        if (threadPriorityBoostCaptured)
        {
            SetThreadPriorityBoost(thread, previousThreadPriorityBoostDisabled);
        }

        if (oldAffinity != 0)
        {
            SetThreadAffinityMask(thread, oldAffinity);
        }

        if (oldIdealProcessor != MAXIMUM_PROCESSORS)
        {
            SetThreadIdealProcessor(thread, oldIdealProcessor);
        }

        if (cpuSetApplied)
        {
            SetThreadSelectedCpuSets(thread, nullptr, 0);
        }
    }

    class PrecisionScope
    {
    public:
        PrecisionScope(const MhpRunOptions& options, MhpRunStats& stats)
            : thread(GetCurrentThread()),
              process(GetCurrentProcess()),
              oldProcessPriority(GetPriorityClass(process)),
              oldThreadPriority(GetThreadPriority(thread))
        {
            BeginTimerResolutionScope();
            BeginPowerThrottlingScope();
            processPriorityBoostCaptured = GetProcessPriorityBoost(process, &previousProcessPriorityBoostDisabled) != FALSE;
            if (processPriorityBoostCaptured)
            {
                if (SetProcessPriorityBoost(process, TRUE) != FALSE)
                {
                    stats.processPriorityBoostDisabled++;
                }
            }

            threadPriorityBoostCaptured = GetThreadPriorityBoost(thread, &previousThreadPriorityBoostDisabled) != FALSE;
            if (threadPriorityBoostCaptured)
            {
                if (SetThreadPriorityBoost(thread, TRUE) != FALSE)
                {
                    stats.threadPriorityBoostDisabled++;
                }
            }

            if (options.precisionMode == MhpUltraLowJitter)
            {
                if (SetPriorityClass(process, HIGH_PRIORITY_CLASS) != FALSE)
                {
                    stats.processPriorityApplied++;
                }
            }
            else
            {
                if (SetPriorityClass(process, HIGH_PRIORITY_CLASS) != FALSE)
                {
                    stats.processPriorityApplied++;
                }
            }

            if (SetThreadPriority(thread, THREAD_PRIORITY_TIME_CRITICAL) != FALSE)
            {
                stats.threadPriorityApplied++;
            }
            avrtHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &avrtTaskIndex);
            if (avrtHandle != nullptr)
            {
                if (AvSetMmThreadPriority(avrtHandle, AVRT_PRIORITY_CRITICAL) != FALSE)
                {
                    stats.mmcssApplied++;
                }
            }

            if (options.precisionMode == MhpUltraLowJitter)
            {
                const bool shouldPinToCore = options.enableCpuScan != 0 && CpuScanCacheReady();
                stats.selectedCpuSet = CurrentProcessorNumber();
                selectedCore = shouldPinToCore ? SelectLowestJitterCore(false) : CoreChoice{};
                if (shouldPinToCore && selectedCore.mask != 0)
                {
                    oldAffinityMask = SetThreadAffinityMask(thread, selectedCore.mask);
                    stats.selectedCpuSet = selectedCore.processorNumber;
                    oldIdealProcessor = SetThreadIdealProcessor(thread, selectedCore.processorNumber);
                    cpuSetApplied = ApplyThreadCpuSetForMask(thread, selectedCore.mask, &stats.cpuSetAppliedCount);
                }
            }
        }

        ~PrecisionScope()
        {
            if (oldAffinityMask != 0)
            {
                SetThreadAffinityMask(thread, oldAffinityMask);
            }

            if (oldIdealProcessor != MAXIMUM_PROCESSORS)
            {
                SetThreadIdealProcessor(thread, oldIdealProcessor);
            }

            if (cpuSetApplied)
            {
                SetThreadSelectedCpuSets(thread, nullptr, 0);
            }

            if (avrtHandle != nullptr)
            {
                AvRevertMmThreadCharacteristics(avrtHandle);
            }

            if (threadPriorityBoostCaptured)
            {
                SetThreadPriorityBoost(thread, previousThreadPriorityBoostDisabled);
            }

            if (processPriorityBoostCaptured)
            {
                SetProcessPriorityBoost(process, previousProcessPriorityBoostDisabled);
            }

            if (oldThreadPriority != THREAD_PRIORITY_ERROR_RETURN)
            {
                SetThreadPriority(thread, oldThreadPriority);
            }

            if (oldProcessPriority != 0)
            {
                SetPriorityClass(process, oldProcessPriority);
            }

            EndTimerResolutionScope();
            EndPowerThrottlingScope();
        }

    private:
        HANDLE thread;
        HANDLE process;
        DWORD oldProcessPriority;
        int oldThreadPriority;
        bool timePeriodActive = false;
        bool ntTimerResolutionActive = false;
        ULONG ntTimerResolution = 0;
        HANDLE avrtHandle = nullptr;
        DWORD avrtTaskIndex = 0;
        DWORD_PTR oldAffinityMask = 0;
        CoreChoice selectedCore{};
        DWORD oldIdealProcessor = MAXIMUM_PROCESSORS;
        bool cpuSetApplied = false;
        bool hasPreviousPowerThrottling = false;
        ProcessPowerThrottlingState previousPowerThrottling{};
        bool powerThrottlingActive = false;
        bool processPriorityBoostCaptured = false;
        BOOL previousProcessPriorityBoostDisabled = FALSE;
        bool threadPriorityBoostCaptured = false;
        BOOL previousThreadPriorityBoostDisabled = FALSE;

        void BeginTimerResolutionScope()
        {
            HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
            auto ntSetTimerResolution = ntdll == nullptr
                ? nullptr
                : reinterpret_cast<NtSetTimerResolutionFn>(
                    GetProcAddress(ntdll, "NtSetTimerResolution"));
            if (ntSetTimerResolution != nullptr)
            {
                ULONG actual = 0;
                if (ntSetTimerResolution(5000, TRUE, &actual) == 0)
                {
                    ntTimerResolutionActive = true;
                    ntTimerResolution = actual;
                }
            }

            timePeriodActive = timeBeginPeriod(1) == TIMERR_NOERROR;
        }

        void EndTimerResolutionScope()
        {
            if (timePeriodActive)
            {
                timeEndPeriod(1);
            }

            if (ntTimerResolutionActive)
            {
                HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
                auto ntSetTimerResolution = ntdll == nullptr
                    ? nullptr
                    : reinterpret_cast<NtSetTimerResolutionFn>(
                        GetProcAddress(ntdll, "NtSetTimerResolution"));
                if (ntSetTimerResolution != nullptr)
                {
                    ULONG actual = 0;
                    ntSetTimerResolution(ntTimerResolution, FALSE, &actual);
                }
            }
        }

        void BeginPowerThrottlingScope()
        {
            previousPowerThrottling.Version = ProcessPowerThrottlingCurrentVersion;
            hasPreviousPowerThrottling = GetProcessInformation(
                process,
                static_cast<PROCESS_INFORMATION_CLASS>(ProcessPowerThrottlingClass),
                &previousPowerThrottling,
                sizeof(previousPowerThrottling)) != FALSE;

            ProcessPowerThrottlingState state{};
            state.Version = ProcessPowerThrottlingCurrentVersion;
            state.ControlMask = ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution;
            state.StateMask = 0;
            powerThrottlingActive = SetProcessInformation(
                process,
                static_cast<PROCESS_INFORMATION_CLASS>(ProcessPowerThrottlingClass),
                &state,
                sizeof(state)) != FALSE;
        }

        void EndPowerThrottlingScope()
        {
            if (!powerThrottlingActive || !hasPreviousPowerThrottling)
            {
                return;
            }

            auto restore = previousPowerThrottling;
            SetProcessInformation(
                process,
                static_cast<PROCESS_INFORMATION_CLASS>(ProcessPowerThrottlingClass),
                &restore,
                sizeof(restore));
        }
    };

    int64_t Percentile(std::vector<int64_t>& values, double percentile);
    void RecordOutlierEvent(
        MhpRunStats& stats,
        const MhpRunOptions& options,
        uint32_t batchIndex,
        int64_t dueTick,
        int64_t actualTick,
        int64_t lateUs,
        DWORD cpu,
        int worker,
        bool loopEnd);

    class StandbyWorkerScope
    {
    public:
        StandbyWorkerScope(
            DWORD_PTR affinityMask,
            uint32_t processorNumber,
            uint32_t* cpuSetAppliedCounter,
            uint32_t* priorityAppliedCounter,
            uint32_t* mmcssAppliedCounter,
            uint32_t* priorityBoostDisabledCounter)
            : thread(GetCurrentThread()),
              oldThreadPriority(GetThreadPriority(thread))
        {
            threadPriorityBoostCaptured = GetThreadPriorityBoost(thread, &previousThreadPriorityBoostDisabled) != FALSE;
            if (threadPriorityBoostCaptured)
            {
                if (SetThreadPriorityBoost(thread, TRUE) != FALSE && priorityBoostDisabledCounter != nullptr)
                {
                    *priorityBoostDisabledCounter = 1;
                }
            }

            if (affinityMask != 0)
            {
                oldAffinityMask = SetThreadAffinityMask(thread, affinityMask);
            }

            if (processorNumber != std::numeric_limits<uint32_t>::max())
            {
                oldIdealProcessor = SetThreadIdealProcessor(thread, processorNumber);
                cpuSetApplied = ApplyThreadCpuSetForMask(thread, affinityMask, cpuSetAppliedCounter);
            }

            if (SetThreadPriority(thread, THREAD_PRIORITY_TIME_CRITICAL) != FALSE && priorityAppliedCounter != nullptr)
            {
                *priorityAppliedCounter = 1;
            }
            avrtHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &avrtTaskIndex);
            if (avrtHandle != nullptr)
            {
                if (AvSetMmThreadPriority(avrtHandle, AVRT_PRIORITY_CRITICAL) != FALSE && mmcssAppliedCounter != nullptr)
                {
                    *mmcssAppliedCounter = 1;
                }
            }
        }

        ~StandbyWorkerScope()
        {
            if (oldAffinityMask != 0)
            {
                SetThreadAffinityMask(thread, oldAffinityMask);
            }

            if (oldIdealProcessor != MAXIMUM_PROCESSORS)
            {
                SetThreadIdealProcessor(thread, oldIdealProcessor);
            }

            if (cpuSetApplied)
            {
                SetThreadSelectedCpuSets(thread, nullptr, 0);
            }

            if (avrtHandle != nullptr)
            {
                AvRevertMmThreadCharacteristics(avrtHandle);
            }

            if (threadPriorityBoostCaptured)
            {
                SetThreadPriorityBoost(thread, previousThreadPriorityBoostDisabled);
            }

            if (oldThreadPriority != THREAD_PRIORITY_ERROR_RETURN)
            {
                SetThreadPriority(thread, oldThreadPriority);
            }
        }

    private:
        HANDLE thread;
        int oldThreadPriority;
        DWORD_PTR oldAffinityMask = 0;
        DWORD oldIdealProcessor = MAXIMUM_PROCESSORS;
        HANDLE avrtHandle = nullptr;
        DWORD avrtTaskIndex = 0;
        bool cpuSetApplied = false;
        bool threadPriorityBoostCaptured = false;
        BOOL previousThreadPriorityBoostDisabled = FALSE;
    };

    struct StandbyBatchResult
    {
        int64_t actualTick = 0;
        int64_t dueTick = 0;
        DWORD cpu = 0;
        int64_t submitDurationUs = 0;
        UINT sent = 0;
        DWORD win32Error = 0;
        uint32_t nativeInputsSubmitted = 0;
        int workerIndex = -1;
        bool completed = false;
    };

    struct StandbyRunContext
    {
        const NativePlan* plan;
        const MhpRunOptions* options;
        volatile long* cancelFlag;
        PlaybackControl* playbackControl;
        int64_t frequency;
        int64_t startTick;
        int64_t wakeStartTick;
        std::array<DWORD_PTR, StandbyWorkerCount> workerMasks;
        std::array<uint32_t, StandbyWorkerCount> workerIndexes;
        HANDLE standbyDoneEvent;
        int activeWorkerCount;
        int64_t helperRescueDelayTicks;
        uint32_t batchCount;
        std::unique_ptr<LONG[]> batchClaims;
        std::unique_ptr<std::atomic<int>[]> batchDoneGates;
        std::unique_ptr<StandbyBatchResult[]> results;
        std::atomic<int> cancelled{ 0 };
        std::atomic<int> failed{ 0 };
        std::atomic<int> finishedWorkers{ 0 };
        std::atomic<uint32_t> cpuSetAppliedCount{ 0 };
        std::atomic<uint32_t> workerPriorityAppliedCount{ 0 };
        std::atomic<uint32_t> workerMmcssAppliedCount{ 0 };
        std::atomic<uint32_t> workerPriorityBoostDisabledCount{ 0 };
        std::atomic<int64_t> firstWorkerStartTick{ 0 };
        std::atomic<int64_t> timelineStartTick{ 0 };
        ModifierChordClock modifierChord;

        StandbyRunContext(
            const NativePlan* plan,
            const MhpRunOptions* options,
            volatile long* cancelFlag,
            PlaybackControl* playbackControl,
            int64_t frequency,
            int64_t startTick,
            int64_t wakeStartTick,
            const std::array<DWORD_PTR, StandbyWorkerCount>& workerMasks,
            const std::array<uint32_t, StandbyWorkerCount>& workerIndexes,
            HANDLE standbyDoneEvent,
            int activeWorkerCount,
            int64_t helperRescueDelayTicks)
            : plan(plan),
              options(options),
              cancelFlag(cancelFlag),
              playbackControl(playbackControl),
              frequency(frequency),
              startTick(startTick),
              wakeStartTick(wakeStartTick),
              workerMasks(workerMasks),
              workerIndexes(workerIndexes),
              standbyDoneEvent(standbyDoneEvent),
              activeWorkerCount(std::clamp(activeWorkerCount, 1, StandbyWorkerCount)),
              helperRescueDelayTicks(helperRescueDelayTicks),
              batchCount(static_cast<uint32_t>(plan->batches.size())),
              batchClaims(std::make_unique<LONG[]>(batchCount)),
              batchDoneGates(std::make_unique<std::atomic<int>[]>(batchCount)),
              results(std::make_unique<StandbyBatchResult[]>(batchCount))
        {
            for (uint32_t i = 0; i < batchCount; i++)
            {
                batchClaims[i] = 0;
                batchDoneGates[i].store(0, std::memory_order_relaxed);
            }
        }
    };

    class StandbyEngine
    {
    public:
        ~StandbyEngine()
        {
            Shutdown();
        }

        MhpStatus Warm(const MhpRunOptions* options, MhpRunStats* stats)
        {
            std::lock_guard<std::mutex> guard(lifecycleMutex);
            if (started)
            {
                FillWarmStats(stats);
                return MhpOk;
            }

            standbyShutdownEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            standbyDoneEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            for (auto& startEvent : standbyStartEvent)
            {
                startEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            }

            if (standbyShutdownEvent == nullptr
                || standbyDoneEvent == nullptr
                || std::any_of(
                    standbyStartEvent.begin(),
                    standbyStartEvent.end(),
                    [](HANDLE handle) { return handle == nullptr; }))
            {
                CloseHandles();
                SetLastWin32ErrorText(L"standby engine event creation failed", GetLastError());
                return MhpWin32Error;
            }

            const bool shouldPinStandbyWorkers = options != nullptr && options->enableCpuScan != 0;
            if (!shouldPinStandbyWorkers)
            {
                for (auto& workerCore : workerCores)
                {
                    workerCore = CoreChoice{};
                    workerCore.index = std::numeric_limits<uint32_t>::max();
                    workerCore.maxLateUs = 0;
                }
            }
            else
            {
                const auto selectedCores = SelectLowestJitterCores(StandbyWorkerCount, true);
                for (int i = 0; i < StandbyWorkerCount; i++)
                {
                    workerCores[i] = i < static_cast<int>(selectedCores.size())
                        ? selectedCores[i]
                        : CoreChoice{};
                    if (workerCores[i].mask == 0)
                    {
                        workerCores[i].index = std::numeric_limits<uint32_t>::max();
                    }
                }
            }

            try
            {
                for (int i = 0; i < StandbyWorkerCount; i++)
                {
                    workers[i] = std::thread(&StandbyEngine::WorkerLoop, this, i);
                }
                started = true;
                FillWarmStats(stats);
                SetLastErrorText(L"");
                return MhpOk;
            }
            catch (...)
            {
                ShutdownLocked();
                SetLastErrorText(L"standby engine thread creation failed");
                return MhpWin32Error;
            }
        }

        void Shutdown()
        {
            std::lock_guard<std::mutex> guard(lifecycleMutex);
            ShutdownLocked();
        }

        bool IsReady() const
        {
            return started
                && std::all_of(
                    standbyStartEvent.begin(),
                    standbyStartEvent.end(),
                    [](HANDLE handle) { return handle != nullptr; })
                && standbyDoneEvent != nullptr;
        }

        MhpStatus Run(
            NativePlan* plan,
            const MhpRunOptions& options,
            volatile long* cancelFlag,
            PlaybackControl* playbackControl,
            MhpRunStats& stats)
        {
            if (!IsReady())
            {
                SetLastErrorText(L"standby unavailable");
                return MhpWin32Error;
            }

            std::lock_guard<std::mutex> guard(runMutex);
            stats.standbyUsed = 1;
            stats.selectedCpuSet = workerCores[0].processorNumber;

            if (plan->batches.empty())
            {
                return MhpOk;
            }

            ResetEvent(standbyDoneEvent);
            const int64_t beforeScope = QueryCounter();
            MhpStatus status = MhpOk;
            {
                PrecisionScope scope(options, stats);
                const int64_t afterPlaybackSetup = QueryCounter();
                stats.nativeRunOverheadUs = ToMicroseconds(afterPlaybackSetup - beforeScope, options.qpcFrequency > 0 ? options.qpcFrequency : QueryFrequency());
                const int64_t frequency = options.qpcFrequency > 0 ? options.qpcFrequency : QueryFrequency();
                const int64_t wakeStart = QueryCounter();
                const bool singleOwnerFastLane = ShouldUseSingleOwnerFastLane(*plan, options, frequency);
                const int activeWorkerCount = StandbyWorkerCount;
                const int64_t helperRescueDelayTicks = singleOwnerFastLane && activeWorkerCount > 1
                    ? HelperRescueDelayTicks(frequency)
                    : 0;
                StandbyRunContext context(
                    plan,
                    &options,
                    cancelFlag,
                    playbackControl,
                    frequency,
                    afterPlaybackSetup,
                    wakeStart,
                    WorkerMasks(),
                    WorkerIndexes(),
                    standbyDoneEvent,
                    activeWorkerCount,
                    helperRescueDelayTicks);

                currentContext.store(&context, std::memory_order_release);
                for (int i = 0; i < context.activeWorkerCount; i++)
                {
                    SetEvent(standbyStartEvent[i]);
                }

                while (WaitForSingleObject(standbyDoneEvent, 25) == WAIT_TIMEOUT)
                {
                    if (IsCancelled(cancelFlag))
                    {
                        context.cancelled.store(1, std::memory_order_release);
                    }
                }

                currentContext.store(nullptr, std::memory_order_release);
                status = AggregateContext(context, stats);
            }

            return status;
        }

    private:
        mutable std::mutex lifecycleMutex;
        std::mutex runMutex;
        std::array<HANDLE, StandbyWorkerCount> standbyStartEvent{};
        HANDLE standbyDoneEvent = nullptr;
        HANDLE standbyShutdownEvent = nullptr;
        std::array<std::thread, StandbyWorkerCount> workers;
        std::atomic<StandbyRunContext*> currentContext{ nullptr };
        bool started = false;
        std::array<CoreChoice, StandbyWorkerCount> workerCores{};

        void WorkerLoop(int workerIndex)
        {
            HANDLE waitHandles[2]{ standbyStartEvent[workerIndex], standbyShutdownEvent };
            while (true)
            {
                const DWORD waitResult = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);
                if (waitResult == WAIT_OBJECT_0 + 1 || waitResult == WAIT_FAILED)
                {
                    return;
                }

                auto* context = currentContext.load(std::memory_order_acquire);
                if (context != nullptr)
                {
                    RunWorker(*context, workerIndex);
                }
            }
        }

        void RunWorker(StandbyRunContext& context, int workerIndex)
        {
            const DWORD_PTR mask = context.workerMasks[workerIndex];
            const uint32_t processorNumber = context.workerIndexes[workerIndex];
            uint32_t cpuSetApplied = 0;
            uint32_t priorityApplied = 0;
            uint32_t mmcssApplied = 0;
            uint32_t priorityBoostDisabled = 0;
            StandbyWorkerScope scope(
                mask,
                processorNumber,
                &cpuSetApplied,
                &priorityApplied,
                &mmcssApplied,
                &priorityBoostDisabled);
            if (cpuSetApplied != 0)
            {
                context.cpuSetAppliedCount.fetch_add(cpuSetApplied, std::memory_order_acq_rel);
            }
            if (priorityApplied != 0)
            {
                context.workerPriorityAppliedCount.fetch_add(priorityApplied, std::memory_order_acq_rel);
            }
            if (mmcssApplied != 0)
            {
                context.workerMmcssAppliedCount.fetch_add(mmcssApplied, std::memory_order_acq_rel);
            }
            if (priorityBoostDisabled != 0)
            {
                context.workerPriorityBoostDisabledCount.fetch_add(priorityBoostDisabled, std::memory_order_acq_rel);
            }
            int64_t expectedStart = 0;
            const int64_t workerStart = QueryCounter();
            if (context.firstWorkerStartTick.compare_exchange_strong(
                expectedStart,
                workerStart,
                std::memory_order_acq_rel))
            {
                context.timelineStartTick.store(workerStart, std::memory_order_release);
            }

            int64_t timelineStart = context.timelineStartTick.load(std::memory_order_acquire);
            while (timelineStart == 0
                && context.cancelled.load(std::memory_order_acquire) == 0
                && !IsCancelled(context.cancelFlag))
            {
                PauseProcessor();
                timelineStart = context.timelineStartTick.load(std::memory_order_acquire);
            }

            for (uint32_t index = 0; index < context.batchCount; index++)
            {
                if (context.cancelled.load(std::memory_order_acquire) != 0
                    || IsCancelled(context.cancelFlag))
                {
                    context.cancelled.store(1, std::memory_order_release);
                    break;
                }

                const auto& batch = context.plan->batches[index];
                int64_t baseDueTick = timelineStart + batch.dueTicks;
                int64_t dueTick = baseDueTick + TotalPausedTicks(context.playbackControl);
                const auto chordEdges = InspectModifierChord(
                    context.plan->inputs.data() + batch.inputOffset,
                    batch.inputCount);
                ExtendDueForModifierChord(
                    baseDueTick,
                    dueTick,
                    chordEdges,
                    context.modifierChord,
                    ModifierChordHoldTicks(context.frequency),
                    TotalPausedTicks(context.playbackControl));
                const int64_t rescueDelayTicks = workerIndex == 0 ? 0 : context.helperRescueDelayTicks;
                if (!SpinUntilControlled(
                        baseDueTick,
                        rescueDelayTicks,
                        context.playbackControl,
                        context.cancelFlag,
                        dueTick))
                {
                    context.cancelled.store(1, std::memory_order_release);
                    break;
                }

                if (context.cancelled.load(std::memory_order_acquire) != 0
                    || IsCancelled(context.cancelFlag))
                {
                    context.cancelled.store(1, std::memory_order_release);
                    break;
                }

                if (context.batchDoneGates[index].load(std::memory_order_acquire) == 0
                    && InterlockedCompareExchange(
                    &context.batchClaims[index],
                    static_cast<LONG>(workerIndex + 1),
                    0) == 0)
                {
                    if (!SpinUntilControlled(
                            baseDueTick,
                            0,
                            context.playbackControl,
                            context.cancelFlag,
                            dueTick))
                    {
                        context.cancelled.store(1, std::memory_order_release);
                        context.batchDoneGates[index].store(1, std::memory_order_release);
                        break;
                    }
                    SubmitStandbyBatch(context, batch, index, workerIndex, dueTick);
                    context.batchDoneGates[index].store(1, std::memory_order_release);
                }

                while (context.batchDoneGates[index].load(std::memory_order_acquire) == 0
                    && context.cancelled.load(std::memory_order_acquire) == 0)
                {
                    PauseProcessor();
                }

                if (context.cancelled.load(std::memory_order_acquire) != 0)
                {
                    break;
                }
            }

            if (context.finishedWorkers.fetch_add(1, std::memory_order_acq_rel) == context.activeWorkerCount - 1)
            {
                SetEvent(context.standbyDoneEvent);
            }
        }

        void SubmitStandbyBatch(
            StandbyRunContext& context,
            const MhpBatch& batch,
            uint32_t index,
            int workerIndex,
            int64_t dueTick)
        {
            auto& result = context.results[index];
            result.actualTick = QueryCounter();
            result.dueTick = dueTick;
            result.cpu = CurrentProcessorNumber();
            result.sent = batch.inputCount;
            result.workerIndex = workerIndex;
            result.completed = true;

            if (batch.inputCount > 0)
            {
                auto* inputPointer = const_cast<INPUT*>(context.plan->inputs.data() + batch.inputOffset);
                const int64_t submitStart = QueryCounter();
                result.sent = SubmitPlaybackInput(batch.inputCount, inputPointer);
                const int64_t submitEnd = QueryCounter();
                result.submitDurationUs = ToMicroseconds(submitEnd - submitStart, context.frequency);
                if (result.sent != batch.inputCount)
                {
                    result.win32Error = GetLastError();
                    context.failed.store(1, std::memory_order_release);
                    context.cancelled.store(1, std::memory_order_release);
                }
                else
                {
                    result.nativeInputsSubmitted = batch.inputCount;
                    context.modifierChord.NoteSent(
                        InspectModifierChord(inputPointer, batch.inputCount),
                        submitEnd);
                }
            }
        }

        MhpStatus AggregateContext(StandbyRunContext& context, MhpRunStats& stats)
        {
            std::vector<int64_t> lateSamples;
            lateSamples.reserve(context.batchCount);
            std::vector<int64_t> loopEndLateSamples;
            if (context.options->loopStepCount > 0)
            {
                loopEndLateSamples.reserve((context.batchCount / static_cast<uint32_t>(context.options->loopStepCount)) + 1);
            }

            const int64_t firstWorkerStart = context.firstWorkerStartTick.load(std::memory_order_acquire);
            stats.engineWakeCostUs = firstWorkerStart == 0
                ? 0
                : ToMicroseconds(firstWorkerStart - context.wakeStartTick, context.frequency);
            stats.waitPathSpinCount += context.batchCount;
            stats.workerPriorityAppliedCount += context.workerPriorityAppliedCount.load(std::memory_order_acquire);
            stats.workerMmcssAppliedCount += context.workerMmcssAppliedCount.load(std::memory_order_acquire);
            stats.workerPriorityBoostDisabledCount += context.workerPriorityBoostDisabledCount.load(std::memory_order_acquire);

            DWORD lastCpu = 0;
            bool hasLastCpu = false;
            MhpStatus status = context.cancelled.load(std::memory_order_acquire) != 0 || IsCancelled(context.cancelFlag)
                ? MhpCancelled
                : MhpOk;

            for (uint32_t index = 0; index < context.batchCount; index++)
            {
                const auto& result = context.results[index];
                if (!result.completed)
                {
                    break;
                }

                const auto& batch = context.plan->batches[index];
                const int64_t dueTick = result.dueTick;
                const int64_t lateUs = std::max<int64_t>(0, ToMicroseconds(result.actualTick - dueTick, context.frequency));
                lateSamples.push_back(lateUs);
                if (lateUs > stats.maxLateUs)
                {
                    stats.maxLateUs = lateUs;
                    stats.maxLateBatchIndex = index;
                    stats.maxLateCpu = result.cpu;
                    stats.maxLateWorker = result.workerIndex;
                }

                const bool isLoopEnd = context.options->loopStepCount > 0
                    && ((index + 1) % static_cast<uint32_t>(context.options->loopStepCount)) == 0;

                if (lateUs > context.options->outlierThresholdUs)
                {
                    stats.outliersOverThreshold++;
                    RecordOutlierEvent(
                        stats,
                        *context.options,
                        index,
                        dueTick,
                        result.actualTick,
                        lateUs,
                        result.cpu,
                        result.workerIndex,
                        isLoopEnd);
                }

                if (isLoopEnd)
                {
                    loopEndLateSamples.push_back(lateUs);
                    stats.maxLoopEndLateUs = std::max(stats.maxLoopEndLateUs, lateUs);
                    if (lateUs > context.options->outlierThresholdUs)
                    {
                        stats.loopEndOutliersOverThreshold++;
                    }
                }

                if (hasLastCpu && result.cpu != lastCpu)
                {
                    stats.cpuMigrationCount++;
                }
                lastCpu = result.cpu;
                hasLastCpu = true;

                if (result.workerIndex == 0)
                {
                    stats.primaryWorkerWins++;
                }
                else if (result.workerIndex >= 1)
                {
                    stats.helperWorkerWins++;
                }

                stats.nativeSubmitDurationUs += result.submitDurationUs;
                if (result.sent != batch.inputCount)
                {
                    stats.failedSubmissions++;
                    stats.lastWin32Error = static_cast<int32_t>(result.win32Error);
                    SetLastWin32ErrorText(L"SendInput submitted a partial standby batch", result.win32Error);
                    status = MhpWin32Error;
                    break;
                }

                stats.nativeInputsSubmitted += result.nativeInputsSubmitted;
                stats.actionsSubmitted += batch.actionCount;
                stats.batchesSubmitted++;
            }

            stats.p999LateUs = Percentile(lateSamples, 0.999);
            stats.p999LoopEndLateUs = Percentile(loopEndLateSamples, 0.999);
            stats.cpuSetAppliedCount = context.cpuSetAppliedCount.load(std::memory_order_acquire);
            if (status == MhpOk)
            {
                SetLastErrorText(L"");
            }

            return status;
        }

        void FillWarmStats(MhpRunStats* stats)
        {
            if (stats == nullptr)
            {
                return;
            }

            *stats = {};
            stats->standbyUsed = started ? 1 : 0;
            stats->selectedCpuSet = workerCores[0].processorNumber;
            stats->cpuSetAppliedCount = 0;
            stats->maxLateCpu = std::numeric_limits<uint32_t>::max();
            stats->maxLateWorker = -1;
            stats->selectedWorker0 = workerCores[0].processorNumber;
            stats->selectedWorker1 = workerCores[1].processorNumber;
            stats->selectedWorker0MaxLateUs = workerCores[0].maxLateUs;
            stats->selectedWorker1MaxLateUs = workerCores[1].maxLateUs;
        }

        std::array<DWORD_PTR, StandbyWorkerCount> WorkerMasks() const
        {
            std::array<DWORD_PTR, StandbyWorkerCount> masks{};
            for (int i = 0; i < StandbyWorkerCount; i++)
            {
                masks[i] = workerCores[i].mask;
            }
            return masks;
        }

        std::array<uint32_t, StandbyWorkerCount> WorkerIndexes() const
        {
            std::array<uint32_t, StandbyWorkerCount> indexes{};
            for (int i = 0; i < StandbyWorkerCount; i++)
            {
                indexes[i] = workerCores[i].processorNumber;
            }
            return indexes;
        }

        void ShutdownLocked()
        {
            if (standbyShutdownEvent != nullptr)
            {
                SetEvent(standbyShutdownEvent);
            }

            for (auto& worker : workers)
            {
                if (worker.joinable())
                {
                    worker.join();
                }
            }

            CloseHandles();
            started = false;
            currentContext.store(nullptr, std::memory_order_release);
        }

        void CloseHandles()
        {
            for (auto& handle : standbyStartEvent)
            {
                if (handle != nullptr)
                {
                    CloseHandle(handle);
                    handle = nullptr;
                }
            }

            if (standbyDoneEvent != nullptr)
            {
                CloseHandle(standbyDoneEvent);
                standbyDoneEvent = nullptr;
            }

            if (standbyShutdownEvent != nullptr)
            {
                CloseHandle(standbyShutdownEvent);
                standbyShutdownEvent = nullptr;
            }
        }
    };

    StandbyEngine g_standbyEngine;

    bool ValidatePlan(const NativePlan& plan)
    {
        for (const auto& batch : plan.batches)
        {
            const uint64_t offset = batch.inputOffset;
            const uint64_t count = batch.inputCount;
            if (offset + count > plan.inputs.size())
            {
                return false;
            }
        }

        return true;
    }

    MhpStatus WaitUntilDeadline(
        int64_t baseDueTick,
        const MhpRunOptions& options,
        int64_t frequency,
        volatile long* cancelFlag,
        PlaybackControl* playbackControl,
        WaitableTimer& timer,
        MhpRunStats& stats,
        int64_t& effectiveDueTick)
    {
        const int64_t finalSpinUs = options.precisionMode == MhpUltraLowJitter ? 2500 : 800;

        while (true)
        {
            if (IsCancelled(cancelFlag))
            {
                return MhpCancelled;
            }

            if (!WaitForResume(playbackControl, cancelFlag))
            {
                return MhpCancelled;
            }

            const int64_t pauseTicks = TotalPausedTicks(playbackControl);
            effectiveDueTick = baseDueTick + pauseTicks;

            const int64_t now = QueryCounter();
            const int64_t remainingTicks = effectiveDueTick - now;
            if (remainingTicks <= 0)
            {
                if (IsPaused(playbackControl) || pauseTicks != TotalPausedTicks(playbackControl))
                {
                    continue;
                }
                stats.waitPathLateCount++;
                return MhpOk;
            }

            const int64_t remainingUs = ToMicroseconds(remainingTicks, frequency);
            if (remainingUs > finalSpinUs + 750)
            {
                const int64_t timerWaitUs = remainingUs - finalSpinUs;
                if (timer.WaitForMicroseconds(timerWaitUs, cancelFlag))
                {
                    stats.waitPathTimerCount++;
                }

                continue;
            }

            stats.waitPathSpinCount++;
            while (!IsCancelled(cancelFlag)
                && !IsPaused(playbackControl)
                && QueryCounter() < effectiveDueTick)
            {
                PauseProcessor();
            }

            if (IsPaused(playbackControl) || pauseTicks != TotalPausedTicks(playbackControl))
            {
                continue;
            }

            return IsCancelled(cancelFlag) ? MhpCancelled : MhpOk;
        }
    }

    int64_t Percentile(std::vector<int64_t>& values, double percentile)
    {
        if (values.empty())
        {
            return 0;
        }

        std::sort(values.begin(), values.end());
        const auto index = static_cast<size_t>(
            std::floor(std::clamp(percentile, 0.0, 1.0) * static_cast<double>(values.size() - 1)));
        return values[index];
    }

    void RecordOutlierEvent(
        MhpRunStats& stats,
        const MhpRunOptions& options,
        uint32_t batchIndex,
        int64_t dueTick,
        int64_t actualTick,
        int64_t lateUs,
        DWORD cpu,
        int worker,
        bool loopEnd)
    {
        const uint32_t slot = stats.outlierEventsWritten++;
        if (options.outlierEvents == nullptr || slot >= options.outlierEventCapacity)
        {
            return;
        }

        auto& output = options.outlierEvents[slot];
        output.batchIndex = batchIndex;
        output.cpu = cpu;
        output.worker = worker;
        output.loopEnd = loopEnd ? 1 : 0;
        output.dueTick = dueTick;
        output.actualTick = actualTick;
        output.lateUs = lateUs;
    }
#ifndef MACROHID_SCHEDULER_SOURCE
#define MACROHID_SCHEDULER_SOURCE "SharedPlaybackScheduler.inl"
#endif
#include MACROHID_SCHEDULER_SOURCE
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpWarmEngine(
    const MhpRunOptions* options,
    MhpRunStats* stats)
{
    MhpRunOptions effectiveOptions{};
    if (options != nullptr)
    {
        effectiveOptions = *options;
    }
    effectiveOptions.precisionMode = effectiveOptions.precisionMode == 0
        ? MhpUltraLowJitter
        : effectiveOptions.precisionMode;
    effectiveOptions.qpcFrequency = effectiveOptions.qpcFrequency > 0
        ? effectiveOptions.qpcFrequency
        : QueryFrequency();
    effectiveOptions.nativeEngineMode = MhpNativeEngineStandby;
    return LegacyPlaybackEnabled(&effectiveOptions) ? g_standbyEngine.Warm(&effectiveOptions, stats) : g_sharedScheduler.Warm(&effectiveOptions, stats);
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpPrepareEngine(const MhpRunOptions* options, uint64_t allowedMask, MhpRunStats* stats)
{
    return LegacyPlaybackEnabled(options) ? MhpWarmEngine(options, stats) : g_sharedScheduler.Warm(options, stats, static_cast<DWORD_PTR>(allowedMask));
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpScanCores(
    MhpCoreSample* samples,
    uint32_t capacity,
    uint32_t* written,
    MhpCoreProgressFn progress)
{
    return MhpScanCoresOnMask(0, samples, capacity, written, progress);
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpScanCoresOnMask(uint64_t mask, MhpCoreSample* samples, uint32_t capacity, uint32_t* written, MhpCoreProgressFn progress)
{
    const auto choices = ScanEligibleCores(true, progress, false, static_cast<DWORD_PTR>(mask));
    uint32_t count = 0;
    if (samples != nullptr)
    {
        for (const auto& choice : choices)
        {
            if (count >= capacity)
            {
                break;
            }

            samples[count].processorNumber = choice.processorNumber;
            samples[count].maxLateUs = choice.maxLateUs;
            count++;
        }
    }

    if (written != nullptr)
    {
        *written = count;
    }

    return MhpOk;
}

extern "C" __declspec(dllexport) void __cdecl MhpShutdownEngine()
{
    g_sharedScheduler.Shutdown();
    g_standbyEngine.Shutdown();
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpCreatePlan(
    const INPUT* inputs,
    uint32_t inputCount,
    const MhpBatch* batches,
    uint32_t batchCount,
    void** plan)
{
    if (plan == nullptr || (inputCount > 0 && inputs == nullptr) || (batchCount > 0 && batches == nullptr))
    {
        SetLastErrorText(L"invalid null argument");
        return MhpInvalidArgument;
    }

    auto nativePlan = new (std::nothrow) NativePlan();
    if (nativePlan == nullptr)
    {
        SetLastErrorText(L"out of memory creating native plan");
        return MhpInvalidArgument;
    }

    try
    {
        if (inputCount > 0) nativePlan->inputs.assign(inputs, inputs + inputCount);
        if (batchCount > 0) nativePlan->batches.assign(batches, batches + batchCount);
        if (!ValidatePlan(*nativePlan)) throw std::invalid_argument("batch input range");
        CompileReleaseDependencies(*nativePlan);
        nativePlan->chordEdges.reserve(nativePlan->batches.size());
        for (const auto& batch : nativePlan->batches)
            nativePlan->chordEdges.push_back(InspectModifierChord(
                batch.inputCount ? nativePlan->inputs.data() + batch.inputOffset : nullptr, batch.inputCount));
        nativePlan->configuredCores = g_sharedScheduler.PreparedCores();
    }
    catch (...) { delete nativePlan; *plan = nullptr; SetLastErrorText(L"invalid plan or allocation failed"); return MhpInvalidArgument; }

    *plan = nativePlan;
    SetLastErrorText(L"");
    return MhpOk;
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpRunPlan(
    void* plan,
    const MhpRunOptions* options,
    volatile long* cancelFlag,
    MhpRunStats* stats)
{
    return MhpRunPlanControlled(plan, options, cancelFlag, nullptr, stats);
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpRunPlanControlled(
    void* plan,
    const MhpRunOptions* options,
    volatile long* cancelFlag,
    void* playbackControl,
    MhpRunStats* stats)
{
    if (plan == nullptr || options == nullptr || stats == nullptr)
    {
        SetLastErrorText(L"invalid null argument");
        return MhpInvalidArgument;
    }

    if (!LegacyPlaybackEnabled(options))
    {
        *stats = {};
        return g_sharedScheduler.Run(static_cast<NativePlan*>(plan), *options, static_cast<PlaybackControl*>(playbackControl), cancelFlag, *stats);
    }

    PlaybackActivityScope playbackActivity;
    *stats = {};
    stats->selectedCpuSet = std::numeric_limits<uint32_t>::max();
    auto* nativePlan = static_cast<NativePlan*>(plan);
    const int64_t frequency = options->qpcFrequency > 0 ? options->qpcFrequency : QueryFrequency();
    stats->maxLateBatchIndex = std::numeric_limits<uint32_t>::max();
    stats->maxLateCpu = std::numeric_limits<uint32_t>::max();
    stats->maxLateWorker = -1;
    auto* control = static_cast<PlaybackControl*>(playbackControl);

    if (options->precisionMode == MhpUltraLowJitter
        && options->nativeEngineMode != MhpNativeEngineInline
        && g_standbyEngine.IsReady())
    {
        auto standbyStatus = g_standbyEngine.Run(nativePlan, *options, cancelFlag, control, *stats);
        if (standbyStatus == MhpOk || standbyStatus == MhpCancelled || options->nativeEngineMode == MhpNativeEngineStandby)
        {
            return standbyStatus;
        }
    }

    if (options->nativeEngineMode == MhpNativeEngineStandby)
    {
        SetLastErrorText(L"standby unavailable");
        return MhpWin32Error;
    }

    std::vector<int64_t> lateSamples;
    lateSamples.reserve(nativePlan->batches.size());
    std::vector<int64_t> loopEndLateSamples;
    if (options->loopStepCount > 0)
    {
        loopEndLateSamples.reserve((nativePlan->batches.size() / static_cast<size_t>(options->loopStepCount)) + 1);
    }

    const int64_t beforeScope = QueryCounter();
    MhpStatus status = MhpOk;
    {
        PrecisionScope scope(*options, *stats);
        WaitableTimer timer;
        DWORD lastCpu = CurrentProcessorNumber();
        uint32_t batchIndex = 0;
        int64_t previousDueTick = 0;
        RedundantWaitState redundantState{};
        redundantState.plan = nativePlan;
        redundantState.frequency = frequency;
        redundantState.cancelFlag = cancelFlag;
        std::vector<std::thread> redundantThreads;
        bool redundantAvailable = false;
        const bool singleOwnerFastLane = control == nullptr
            && ShouldUseSingleOwnerFastLane(*nativePlan, *options, frequency);
        redundantState.helperRescueDelayTicks = singleOwnerFastLane ? HelperRescueDelayTicks(frequency) : 0;
        if (options->precisionMode == MhpUltraLowJitter)
        {
            const int helperThreadCount = singleOwnerFastLane ? 0 : 1;
            const auto helperMasks = SelectHelperCoreMasks(0, helperThreadCount);
            redundantThreads.reserve(helperMasks.size());
            for (const auto helperMask : helperMasks)
            {
                try
                {
                    redundantThreads.emplace_back(RunRedundantWaiter, &redundantState, helperMask);
                    redundantAvailable = true;
                }
                catch (...)
                {
                    // Keep any helper threads that were already started.
                }
            }
        }
        const int64_t afterPlaybackSetup = QueryCounter();
        stats->nativeRunOverheadUs = ToMicroseconds(afterPlaybackSetup - beforeScope, frequency);
        const int64_t startTick = afterPlaybackSetup;
        ModifierChordClock modifierChord;
        const int64_t modifierHoldTicks = ModifierChordHoldTicks(frequency);

        for (const auto& batch : nativePlan->batches)
        {
            int64_t baseDueTick = startTick + batch.dueTicks;
            int64_t dueTick = baseDueTick + TotalPausedTicks(control);
            const auto chordEdges = InspectModifierChord(
                nativePlan->inputs.data() + batch.inputOffset,
                batch.inputCount);
            ExtendDueForModifierChord(
                baseDueTick,
                dueTick,
                chordEdges,
                modifierChord,
                modifierHoldTicks,
                TotalPausedTicks(control));
            int64_t actualTick = 0;
            int64_t submitDurationUs = 0;
            UINT sent = batch.inputCount;
            DWORD win32Error = 0;
            uint32_t nativeInputsSubmitted = 0;
            DWORD currentCpu = lastCpu;
            int workerIndex = 0;
            const bool useRedundantWaiter = control == nullptr
                && redundantAvailable
                && ShouldUseRedundantWaiter(*options, previousDueTick, startTick, dueTick, frequency);

            if (useRedundantWaiter)
            {
                stats->waitPathSpinCount++;
                redundantState.batch = &batch;
                redundantState.dueTick = dueTick;
                redundantState.claimed.store(0, std::memory_order_release);
                redundantState.completed.store(0, std::memory_order_release);
                redundantState.generation.fetch_add(1, std::memory_order_release);

                AttemptRedundantSubmit(redundantState, false);
                while (redundantState.completed.load(std::memory_order_acquire) == 0)
                {
                    if (IsCancelled(cancelFlag) && redundantState.claimed.load(std::memory_order_acquire) == 0)
                    {
                        status = MhpCancelled;
                        break;
                    }

                    PauseProcessor();
                }

                if (status == MhpCancelled)
                {
                    break;
                }

                actualTick = redundantState.actualTick;
                submitDurationUs = redundantState.submitDurationUs;
                sent = redundantState.sent;
                win32Error = redundantState.win32Error;
                nativeInputsSubmitted = redundantState.nativeInputsSubmitted;
                currentCpu = redundantState.cpu;
                workerIndex = redundantState.helperWon ? 1 : 0;
            }
            else
            {
                status = WaitUntilDeadline(
                    baseDueTick,
                    *options,
                    frequency,
                    cancelFlag,
                    control,
                    timer,
                    *stats,
                    dueTick);
                if (status == MhpCancelled)
                {
                    break;
                }

                actualTick = QueryCounter();
                currentCpu = CurrentProcessorNumber();
                if (batch.inputCount > 0)
                {
                    auto* inputPointer = nativePlan->inputs.data() + batch.inputOffset;
                    const int64_t submitStart = QueryCounter();
                    sent = SubmitPlaybackInput(batch.inputCount, inputPointer);
                    const int64_t submitEnd = QueryCounter();
                    submitDurationUs = ToMicroseconds(submitEnd - submitStart, frequency);
                    if (sent != batch.inputCount)
                    {
                        win32Error = GetLastError();
                    }
                    else
                    {
                        nativeInputsSubmitted = batch.inputCount;
                    }
                }
            }

            const int64_t lateUs = std::max<int64_t>(0, ToMicroseconds(actualTick - dueTick, frequency));
            lateSamples.push_back(lateUs);
            if (lateUs > stats->maxLateUs)
            {
                stats->maxLateUs = lateUs;
                stats->maxLateBatchIndex = batchIndex;
                stats->maxLateCpu = currentCpu;
                stats->maxLateWorker = workerIndex;
            }
            const bool isLoopEnd = options->loopStepCount > 0
                && ((batchIndex + 1) % static_cast<uint32_t>(options->loopStepCount)) == 0;

            if (lateUs > options->outlierThresholdUs)
            {
                stats->outliersOverThreshold++;
                RecordOutlierEvent(
                    *stats,
                    *options,
                    batchIndex,
                    dueTick,
                    actualTick,
                    lateUs,
                    currentCpu,
                    workerIndex,
                    isLoopEnd);
            }
            if (isLoopEnd)
            {
                loopEndLateSamples.push_back(lateUs);
                stats->maxLoopEndLateUs = std::max(stats->maxLoopEndLateUs, lateUs);
                if (lateUs > options->outlierThresholdUs)
                {
                    stats->loopEndOutliersOverThreshold++;
                }
            }

            if (currentCpu != lastCpu)
            {
                stats->cpuMigrationCount++;
                lastCpu = currentCpu;
            }

            if (workerIndex == 0)
            {
                stats->primaryWorkerWins++;
            }
            else if (workerIndex == 1)
            {
                stats->helperWorkerWins++;
            }

            stats->nativeSubmitDurationUs += submitDurationUs;
            if (sent != batch.inputCount)
            {
                stats->failedSubmissions++;
                stats->lastWin32Error = static_cast<int32_t>(win32Error);
                SetLastWin32ErrorText(L"SendInput submitted a partial batch", stats->lastWin32Error);
                status = MhpWin32Error;
                break;
            }

            stats->nativeInputsSubmitted += nativeInputsSubmitted;
            stats->actionsSubmitted += batch.actionCount;
            stats->batchesSubmitted++;
            modifierChord.NoteSent(chordEdges, QueryCounter());
            batchIndex++;
            previousDueTick = dueTick;
        }

        if (!redundantThreads.empty())
        {
            redundantState.stop.store(1, std::memory_order_release);
            redundantState.generation.fetch_add(1, std::memory_order_release);
            for (auto& redundantThread : redundantThreads)
            {
                if (redundantThread.joinable())
                {
                    redundantThread.join();
                }
            }
        }
    }

    stats->p999LateUs = Percentile(lateSamples, 0.999);
    stats->p999LoopEndLateUs = Percentile(loopEndLateSamples, 0.999);
    if (status == MhpOk)
    {
        SetLastErrorText(L"");
    }
    return status;
}

extern "C" __declspec(dllexport) void* __cdecl MhpCreatePlaybackControl()
{
    try
    {
        return new PlaybackControl();
    }
    catch (...)
    {
        return nullptr;
    }
}

extern "C" __declspec(dllexport) void __cdecl MhpPausePlayback(void* playbackControl)
{
    auto* control = static_cast<PlaybackControl*>(playbackControl);
    if (control == nullptr)
    {
        return;
    }

    if (InterlockedIncrement(&control->pauseCount) == 1)
    {
        InterlockedExchange64(&control->pauseStartedTick, QueryCounter());
        InterlockedExchange(&control->pauseFlag, 1);
        g_sharedScheduler.Notify();
    }
}

extern "C" __declspec(dllexport) void __cdecl MhpResumePlayback(void* playbackControl)
{
    auto* control = static_cast<PlaybackControl*>(playbackControl);
    if (control == nullptr)
    {
        return;
    }

    const long remaining = InterlockedDecrement(&control->pauseCount);
    if (remaining > 0)
    {
        return;
    }

    if (remaining < 0)
    {
        InterlockedExchange(&control->pauseCount, 0);
    }

    const int64_t started = InterlockedExchange64(&control->pauseStartedTick, 0);
    if (started > 0)
    {
        InterlockedAdd64(&control->totalPausedTicks, std::max<int64_t>(0, QueryCounter() - started));
    }
    InterlockedExchange(&control->pauseFlag, 0);
    WakeByAddressAll(const_cast<long*>(&control->pauseFlag));
    g_sharedScheduler.Notify();
}

extern "C" __declspec(dllexport) void __cdecl MhpDestroyPlaybackControl(void* playbackControl)
{
    auto* control = static_cast<PlaybackControl*>(playbackControl);
    if (control && control->references.fetch_sub(1) == 1) delete control;
}

extern "C" __declspec(dllexport) void __cdecl MhpCancel(volatile long* cancelFlag)
{
    g_sharedScheduler.Notify();
    if (cancelFlag != nullptr)
    {
        InterlockedExchange(cancelFlag, 1);
        g_sharedScheduler.Notify();
    }
}

extern "C" __declspec(dllexport) void __cdecl MhpDestroyPlan(void* plan)
{
    auto* nativePlan = static_cast<NativePlan*>(plan);
    if (nativePlan && nativePlan->references.fetch_sub(1) == 1) delete nativePlan;
}

struct AsyncPlaybackRun
{
    std::shared_ptr<ScheduledRun> run;
    NativePlan* plan;
    PlaybackControl* control;
};

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpSubmitPlan(void* plan, const MhpRunOptions* options, void* playbackControl, void** runHandle, HANDLE* completion)
{
    return MhpSubmitPlanAt(plan, options, playbackControl, 0, runHandle, completion);
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpSubmitPlanAt(void* plan, const MhpRunOptions* options, void* playbackControl, int64_t timelineStart, void** runHandle, HANDLE* completion)
{
    if (!plan || !options || !runHandle || !completion) return MhpInvalidArgument;
    *runHandle = nullptr; *completion = nullptr;
    auto* p = static_cast<NativePlan*>(plan);
    auto* c = static_cast<PlaybackControl*>(playbackControl);
    p->references.fetch_add(1);
    if (c) c->references.fetch_add(1);
    try
    {
        // Allocate the public owner before publishing to the workers.
        auto owned = std::make_unique<AsyncPlaybackRun>();
        owned->plan = p; owned->control = c;
        owned->run = g_sharedScheduler.Submit(p, *options, c, nullptr, timelineStart);
        if (owned->run)
        {
            *completion = owned->run->completed;
            *runHandle = owned.release();
            return MhpOk;
        }
        SetLastErrorText(L"shared scheduler unavailable");
    }
    catch (...) { SetLastErrorText(L"shared scheduler allocation failed before submission"); }
    MhpDestroyPlan(p); MhpDestroyPlaybackControl(c);
    return MhpWin32Error;
}

extern "C" __declspec(dllexport) void __cdecl MhpCancelRun(void* handle)
{
    if (auto* owned = static_cast<AsyncPlaybackRun*>(handle)) owned->run->cancelled.store(true, std::memory_order_release);
    g_sharedScheduler.Notify();
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpGetRunResult(void* handle, MhpRunStats* stats)
{
    auto* owned = static_cast<AsyncPlaybackRun*>(handle);
    if (!owned || !stats || WaitForSingleObject(owned->run->completed, 0) != WAIT_OBJECT_0) return MhpInvalidArgument;
    *stats = owned->run->stats;
    stats->p999LateUs = Percentile(owned->run->late, .999);
    stats->p999LoopEndLateUs = Percentile(owned->run->loopLate, .999);
    return owned->run->status;
}

extern "C" __declspec(dllexport) void __cdecl MhpReleaseRun(void* handle)
{
    auto* owned = static_cast<AsyncPlaybackRun*>(handle);
    if (!owned) return;
    MhpCancelRun(handle);
    WaitForSingleObject(owned->run->completed, INFINITE);
    MhpDestroyPlan(owned->plan); MhpDestroyPlaybackControl(owned->control);
    delete owned;
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpGetRunTrace(void* handle, MhpRunTrace* trace)
{
    auto* owned = static_cast<AsyncPlaybackRun*>(handle);
    if (!owned || !trace || WaitForSingleObject(owned->run->completed, 0) != WAIT_OBJECT_0) return MhpInvalidArgument;
    *trace = owned->run->trace;
    return MhpOk;
}

extern "C" __declspec(dllexport) MhpStatus __cdecl MhpGetBatchTrace(void* handle, MhpBatchTrace* records, uint32_t capacity, uint32_t* written)
{
    auto* owned = static_cast<AsyncPlaybackRun*>(handle);
    if (!owned || !written || (capacity && !records) || WaitForSingleObject(owned->run->completed, 0) != WAIT_OBJECT_0) return MhpInvalidArgument;
    const auto& trace = owned->run->batchTrace;
    *written = static_cast<uint32_t>(trace.size());
    for (uint32_t i = 0; i < capacity && i < trace.size(); i++) records[i] = trace[i];
    return MhpOk;
}

extern "C" __declspec(dllexport) const wchar_t* __cdecl MhpGetLastErrorText()
{
    return g_lastError.c_str();
}
