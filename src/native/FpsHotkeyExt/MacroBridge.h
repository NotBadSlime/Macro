#pragma once
#include "FpsCore.h"

// Local\\FpsHotkeyExt.<pid>, ten int32 fields. Request/ack sequences publish last.
namespace MacroBridge {
static HANDLE mapping = nullptr;
static volatile LONG* fields = nullptr;
static bool Init() {
    wchar_t name[80]{};
    swprintf_s(name, L"Local\\FpsHotkeyExt.%lu", GetCurrentProcessId());
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 40, name);
    if (!mapping) return false;
    fields = static_cast<volatile LONG*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 40));
    if (!fields) { CloseHandle(mapping); mapping = nullptr; return false; }
    fields[1] = 1;
    InterlockedExchange(&fields[0], 0x46504533);
    return true;
}
static void Poll() {
    if (!fields) return;
    InterlockedExchange(&fields[2], FpsCore::GetFpsInternal());
    InterlockedExchange(&fields[3], FpsCore::g_restoreFps.load());
    InterlockedExchange(&fields[4], FpsCore::g_initialized.load() ? 1 : 0);
    const LONG request = InterlockedCompareExchange(&fields[5], 0, 0);
    if (request == InterlockedCompareExchange(&fields[6], 0, 0)) return;
    const LONG command = InterlockedCompareExchange(&fields[7], 0, 0);
    const int result = command == -1 ? FpsCore::RestoreFpsInternal() :
        (command >= 0 && command <= 1000 ? FpsCore::SetFpsInternal(command) : -2);
    InterlockedExchange(&fields[8], result);
    InterlockedExchange(&fields[2], FpsCore::GetFpsInternal());
    InterlockedExchange(&fields[6], request);
}
}
