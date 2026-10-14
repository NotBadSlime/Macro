#pragma once
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <string>
#include <vector>
#include "vendor/MinHook/include/MinHook.h"

namespace FpsCore {
using LogFn = void(*)(const char*, ...);
using Setter = void (WINAPI*)(int);
using Getter = int (WINAPI*)();
static LogFn g_coreLogger = nullptr;
static Setter g_fnSetFrameCount = nullptr;
static Setter g_originalSetter = nullptr;
static Getter g_fnGetFrameCount = nullptr;
static std::atomic<int> g_currentFps{0};
static std::atomic<bool> g_override{false};
static std::atomic<bool> g_initialized{false};
static std::atomic<int> g_restoreFps{0};
#define FC_LOG(...) do { if (g_coreLogger) g_coreLogger(__VA_ARGS__); } while (0)

static bool ParsePattern(const char* pattern, std::vector<int>& bytes) {
    bytes.clear();
    if (!pattern) return false;
    std::istringstream stream(pattern);
    std::string token;
    while (stream >> token) {
        if (token == "?" || token == "??") { bytes.push_back(-1); continue; }
        if (token.size() != 2) return false;
        int value = 0;
        for (char c : token) {
            int digit = c >= '0' && c <= '9' ? c - '0' :
                c >= 'a' && c <= 'f' ? c - 'a' + 10 :
                c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) return false;
            value = value * 16 + digit;
        }
        bytes.push_back(value);
    }
    return !bytes.empty();
}

// Return the number of matches so ambiguity cannot be mistaken for no match.
static size_t ScanMatches(uint8_t* base, size_t size, const std::vector<int>& bytes, size_t* offset) {
    if (!base || !offset || bytes.empty() || bytes.size() > size) return 0;
    size_t count = 0;
    for (size_t pos = 0; pos <= size - bytes.size(); ++pos) {
        if (bytes[0] >= 0 && base[pos] != bytes[0]) continue;
        size_t i = 1;
        for (; i < bytes.size(); ++i)
            if (bytes[i] >= 0 && base[pos + i] != bytes[i]) break;
        if (i == bytes.size()) {
            *offset = pos;
            if (++count > 1) return count;
        }
    }
    return count;
}

[[maybe_unused]] static bool ScanPattern(uint8_t* base, size_t size, const char* pattern, size_t* offset) {
    std::vector<int> bytes;
    return ParsePattern(pattern, bytes) && ScanMatches(base, size, bytes, offset) == 1;
}

static bool IsExecutable(DWORD protection) {
    return !(protection & (PAGE_GUARD | PAGE_NOACCESS)) &&
        (protection & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY));
}

static uint8_t* ResolveCall(const char* pattern) {
    std::vector<int> bytes;
    if (!ParsePattern(pattern, bytes) || bytes.size() < 5 || bytes[0] != 0xE8) return nullptr;
    auto base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    if (!base) return nullptr;
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return nullptr;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
    const size_t imageSize = nt->OptionalHeader.SizeOfImage;
    auto sections = IMAGE_FIRST_SECTION(nt);
    uint8_t* result = nullptr;
    // Packed games can unpack code outside .text. Query permissions before reading.
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        const auto& section = sections[i];
        if (!(section.Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
        size_t start = section.VirtualAddress;
        size_t length = section.Misc.VirtualSize;
        if (start >= imageSize || length > imageSize - start) continue;
        auto cursor = base + start;
        auto end = cursor + length;
        while (cursor < end) {
            MEMORY_BASIC_INFORMATION info{};
            if (!VirtualQuery(cursor, &info, sizeof(info))) break;
            auto regionEnd = static_cast<uint8_t*>(info.BaseAddress) + info.RegionSize;
            auto readableEnd = regionEnd < end ? regionEnd : end;
            if (readableEnd <= cursor) break;
            if (info.State == MEM_COMMIT && IsExecutable(info.Protect)) {
                size_t off = 0;
                const size_t count = ScanMatches(cursor, readableEnd - cursor, bytes, &off);
                if (count > 1) return nullptr;
                if (count == 1) {
                    int32_t displacement = 0;
                    memcpy(&displacement, cursor + off + 1, sizeof(displacement));
                    const intptr_t target = reinterpret_cast<intptr_t>(cursor + off + 5) + displacement;
                    const intptr_t imageStart = reinterpret_cast<intptr_t>(base);
                    MEMORY_BASIC_INFORMATION targetInfo{};
                    if (target < imageStart || static_cast<uintptr_t>(target - imageStart) >= imageSize ||
                        !VirtualQuery(reinterpret_cast<void*>(target), &targetInfo, sizeof(targetInfo)) ||
                        targetInfo.State != MEM_COMMIT || !IsExecutable(targetInfo.Protect)) return nullptr;
                    if (result) return nullptr;
                    result = reinterpret_cast<uint8_t*>(target);
                }
            }
            cursor = readableEnd;
        }
    }
    return result;
}

static void WINAPI DetourSetFrameCount(int requested) {
    // Include launcher setter calls. Before the first hotkey preserve its target.
    const bool overriding = g_override.load();
    if (!overriding) g_currentFps.store(requested);
    const int target = overriding ? g_currentFps.load() : requested;
    g_originalSetter(target);
}

static bool Init(LogFn logger) {
    g_coreLogger = logger;
    if (g_initialized.load()) return true;
    auto setter = reinterpret_cast<Setter>(ResolveCall(
        "E8 ? ? ? ? E8 ? ? ? ? 83 F8 1F 0F 9C 05 ? ? ? ? 48 8B 05 ? ? ? ?"));
    if (!setter) {
        FC_LOG("[FpsCore] Setter not ready / signature not unique; will retry");
        return false;
    }
    auto status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        FC_LOG("[FpsCore] MH_Initialize failed: %d", status); return false;
    }
    status = MH_CreateHook(reinterpret_cast<void*>(setter), reinterpret_cast<void*>(DetourSetFrameCount),
        reinterpret_cast<void**>(&g_originalSetter));
    if (status != MH_OK) {
        FC_LOG("[FpsCore] Setter hook creation failed: %d", status); return false;
    }
    status = MH_EnableHook(reinterpret_cast<void*>(setter));
    if (status != MH_OK) {
        MH_RemoveHook(reinterpret_cast<void*>(setter));
        FC_LOG("[FpsCore] Setter hook enable failed: %d", status); return false;
    }
    g_fnSetFrameCount = setter;
    g_fnGetFrameCount = reinterpret_cast<Getter>(ResolveCall(
        "E8 ? ? ? ? 85 C0 7E 0E E8 ? ? ? ? 0F 57 C0 F3 0F 2A C0 EB 08"));
    // Prefer KY3's configured startup target: its getter hook may clamp readings.
    int baseline = g_currentFps.load();
    if (baseline <= 0 && g_fnGetFrameCount) baseline = g_fnGetFrameCount();
    if (HMODULE ky3 = GetModuleHandleW(L"Ky3-luancher-Plugin.dll")) {
        wchar_t path[32768]{};
        if (GetModuleFileNameW(ky3, path, _countof(path))) {
            std::wstring ini(path);
            ini = ini.substr(0, ini.find_last_of(L"\\/") + 1) + L"dll_config.ini";
            if (GetPrivateProfileIntW(L"Settings", L"enableFps", 0, ini.c_str())) {
                int configured = static_cast<int>(GetPrivateProfileIntW(L"Settings", L"targetFps", 0, ini.c_str()));
                if (configured > 0 && configured <= 1000) baseline = configured;
            }
        }
    }
    if (baseline > 0 && baseline <= 1000) g_restoreFps.store(baseline);
    g_initialized.store(true);
    FC_LOG("[FpsCore] Ready: setter=%p getter=%p (setter override installed)",
        reinterpret_cast<void*>(setter), reinterpret_cast<void*>(g_fnGetFrameCount));
    return true;
}

static void Tick() {
    if (g_initialized.load() && g_override.load()) g_originalSetter(g_currentFps.load());
}

static int SetFpsInternal(int target) {
    if (target < 0) return -2;
    if (!g_initialized.load()) return -1;
    if (g_restoreFps.load() <= 0) {
        int baseline = g_currentFps.load();
        if (baseline <= 0 && g_fnGetFrameCount) baseline = g_fnGetFrameCount();
        if (baseline > 0 && baseline <= 1000) g_restoreFps.store(baseline);
    }
    const int normalized = target == 0 ? 999 : (target > 1000 ? 1000 : target);
    g_currentFps.store(normalized);
    g_override.store(true);
    g_originalSetter(normalized);
    FC_LOG("[FpsCore] Target FPS=%d (requested=%d)", normalized, target);
    return 0;
}

static int RestoreFpsInternal() {
    if (!g_initialized.load()) return -1;
    const int baseline = g_restoreFps.load();
    if (baseline <= 0) return -3; // Do not guess a startup target.
    g_currentFps.store(baseline);
    g_override.store(false);
    g_originalSetter(baseline);
    FC_LOG("[FpsCore] Restored startup FPS=%d; override released", baseline);
    return 0;
}

static int GetFpsInternal() {
    if (!g_initialized.load()) return -1;
    if (g_currentFps.load() > 0) return g_currentFps.load();
    return g_fnGetFrameCount ? g_fnGetFrameCount() : 0;
}
}
