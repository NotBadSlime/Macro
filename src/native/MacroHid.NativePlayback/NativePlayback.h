#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <Windows.h>
#include <cstdint>

extern "C"
{
    enum MhpStatus : int32_t
    {
        MhpOk = 0,
        MhpInvalidArgument = 1,
        MhpWin32Error = 2,
        MhpCancelled = 3
    };

    enum MhpPrecisionMode : int32_t
    {
        MhpBalanced = 0,
        MhpExtremeDuringPlayback = 1,
        MhpUltraLowJitter = 2
    };

    enum MhpNativeEngineMode : int32_t
    {
        MhpNativeEngineAuto = 0,
        MhpNativeEngineStandby = 1,
        MhpNativeEngineInline = 2
    };

    struct MhpBatch
    {
        int64_t dueTicks;
        uint32_t inputOffset;
        uint32_t inputCount;
        uint32_t actionCount;
    };

    struct MhpOutlierEvent
    {
        uint32_t batchIndex;
        uint32_t cpu;
        int32_t worker;
        int32_t loopEnd;
        int64_t dueTick;
        int64_t actualTick;
        int64_t lateUs;
    };

    struct MhpRunOptions
    {
        int32_t precisionMode;
        int32_t enableCpuScan;
        int32_t outlierThresholdUs;
        int32_t loopStepCount;
        int64_t qpcFrequency;
        int32_t nativeEngineMode;
        MhpOutlierEvent* outlierEvents;
        uint32_t outlierEventCapacity;
    };

    struct MhpRunStats
    {
        uint32_t batchesSubmitted;
        uint32_t actionsSubmitted;
        uint32_t nativeInputsSubmitted;
        uint32_t failedSubmissions;
        uint32_t selectedCpuSet;
        uint32_t cpuMigrationCount;
        uint32_t cpuSetAppliedCount;
        uint32_t outliersOverThreshold;
        uint32_t loopEndOutliersOverThreshold;
        int64_t maxLateUs;
        int64_t p999LateUs;
        int64_t maxLoopEndLateUs;
        int64_t p999LoopEndLateUs;
        int64_t nativeRunOverheadUs;
        int64_t nativeSubmitDurationUs;
        uint32_t waitPathSpinCount;
        uint32_t waitPathTimerCount;
        uint32_t waitPathLateCount;
        int32_t lastWin32Error;
        uint32_t standbyUsed;
        uint32_t primaryWorkerWins;
        uint32_t helperWorkerWins;
        int64_t engineWakeCostUs;
        uint32_t maxLateBatchIndex;
        uint32_t maxLateCpu;
        int32_t maxLateWorker;
        uint32_t outlierEventsWritten;
        uint32_t processPriorityApplied;
        uint32_t threadPriorityApplied;
        uint32_t mmcssApplied;
        uint32_t processPriorityBoostDisabled;
        uint32_t threadPriorityBoostDisabled;
        uint32_t workerPriorityAppliedCount;
        uint32_t workerMmcssAppliedCount;
        uint32_t workerPriorityBoostDisabledCount;
    };

    __declspec(dllexport) MhpStatus __cdecl MhpWarmEngine(
        const MhpRunOptions* options,
        MhpRunStats* stats);

    __declspec(dllexport) void __cdecl MhpShutdownEngine();

    __declspec(dllexport) MhpStatus __cdecl MhpCreatePlan(
        const INPUT* inputs,
        uint32_t inputCount,
        const MhpBatch* batches,
        uint32_t batchCount,
        void** plan);

    __declspec(dllexport) MhpStatus __cdecl MhpRunPlan(
        void* plan,
        const MhpRunOptions* options,
        volatile long* cancelFlag,
        MhpRunStats* stats);

    __declspec(dllexport) void __cdecl MhpCancel(volatile long* cancelFlag);

    __declspec(dllexport) void __cdecl MhpDestroyPlan(void* plan);

    __declspec(dllexport) const wchar_t* __cdecl MhpGetLastErrorText();
}
