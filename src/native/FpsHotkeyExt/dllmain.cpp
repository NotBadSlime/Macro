// FpsHotkeyExt v2.3 - standalone hotkeys with setter override.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <tlhelp32.h>
#include <vector>
#include <string>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdarg>
#include <mutex>
#include "FpsCore.h"
#include "MacroBridge.h"

static HMODULE g_module = nullptr;
static std::wstring g_configPath;
static std::recursive_mutex g_rulesMutex;

// ============================================================
// Logging Implementation
// ============================================================
namespace LogExt {
    static std::wstring g_logPath = L"fps_hotkey_ext.log";
    static std::mutex g_logMutex;
    static HANDLE g_hLog = INVALID_HANDLE_VALUE;
    static const int MAX_LINE_LEN = 512;

    static void Write(const char* msg, size_t len) {
        std::lock_guard<std::mutex> lock(g_logMutex);
        if (g_hLog == INVALID_HANDLE_VALUE) {
            g_hLog = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        }
        if (g_hLog != INVALID_HANDLE_VALUE) {
            DWORD written = 0;
            SetFilePointer(g_hLog, 0, nullptr, FILE_END);
            WriteFile(g_hLog, msg, (DWORD)len, &written, nullptr);
        }
    }

    static void Print(const char* fmt, ...) {
        SYSTEMTIME st;
        GetLocalTime(&st);
        char line[MAX_LINE_LEN];
        int n = snprintf(line, sizeof(line), "[%02d:%02d:%02d] ", st.wHour, st.wMinute, st.wSecond);
        va_list args;
        va_start(args, fmt);
        vsnprintf(line + n, sizeof(line) - n - 2, fmt, args);
        va_end(args);
        size_t length = strlen(line);
        if (length < MAX_LINE_LEN - 2) { line[length] = '\n'; line[length + 1] = '\0'; }
        OutputDebugStringA(line);
        Write(line, strlen(line));
    }
}
#define LOG_EXT(...) LogExt::Print(__VA_ARGS__)

// FpsCore log adapter
static void FpsCoreLogger(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    char buf[512];
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    LOG_EXT("%s", buf);
}

// ============================================================
// Rule struct
// ============================================================
struct FpsRule {
    int id;
    int vk;      // virtual key code
    int fps;     // target fps (0 = unlimited)
    bool enabled;
};

// ============================================================
// Global state
// ============================================================
static std::vector<FpsRule> g_rules;
static int g_nextId = 1;
static int g_restoreKey = VK_F9;
static bool g_restoreEnabled = true;
static HANDLE g_monStopEvent = nullptr;

// ============================================================
// JSON rule file loading/saving
// ============================================================
static bool LoadRulesFromFile(const wchar_t* path) {
    std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD sz = GetFileSize(h, nullptr);
    if (sz == INVALID_FILE_SIZE || sz > 1024 * 1024) { CloseHandle(h); return false; }
    std::vector<char> buf(sz + 1);
    DWORD r;
    if (!ReadFile(h, buf.data(), sz, &r, nullptr)) {
        CloseHandle(h); return false;
    }
    CloseHandle(h);
    buf[r] = '\0';

    std::string json(buf.data());
    g_rules.clear();
    g_restoreKey = VK_F9;
    g_restoreEnabled = true;
    const size_t restorePos = json.find("\"restoreHotkey\"");
    if (restorePos != std::string::npos) {
        const size_t start = json.find('{', restorePos);
        const size_t end = start == std::string::npos ? start : json.find('}', start);
        if (end != std::string::npos) {
            const std::string block = json.substr(start, end - start + 1);
            size_t key = block.find("\"virtualKey\"");
            if (key != std::string::npos) {
                size_t colon = block.find(':', key);
                if (colon != std::string::npos) {
                    try {
                        int vk = std::stoi(block.substr(colon + 1));
                        if (vk > 0 && vk <= 255) g_restoreKey = vk;
                        else return false;
                    } catch (...) { return false; }
                }
            }
            key = block.find("\"enabled\"");
            if (key != std::string::npos) {
                size_t colon = block.find(':', key);
                size_t value = colon == std::string::npos ? colon : block.find_first_not_of(" \t\r\n", colon + 1);
                g_restoreEnabled = value != std::string::npos && block.compare(value, 4, "true") == 0;
            }
        }
    }

    size_t pos = 0;
    while (true) {
        size_t idPos = json.find("\"id\"", pos);
        if (idPos == std::string::npos) break;
        size_t braceStart = json.rfind('{', idPos);
        if (braceStart == std::string::npos) break;
        size_t braceEnd = json.find('}', idPos);
        if (braceEnd == std::string::npos) break;

        std::string block = json.substr(braceStart, braceEnd - braceStart + 1);

        FpsRule rule;
        rule.id = rule.vk = rule.fps = 0; rule.enabled = true;

        auto getInt = [&](const char* key) -> int {
            std::string k = std::string("\"") + key + "\"";
            size_t p = block.find(k); if (p == std::string::npos) return 0;
            p = block.find(':', p + k.size()); if (p == std::string::npos) return 0;
            p = block.find_first_not_of(" \t\r\n", p + 1);
            if (p == std::string::npos || (block[p] != '-' && (block[p] < '0' || block[p] > '9'))) return 0;
            try { return std::stoi(block.substr(p)); } catch (...) { return 0; }
        };
        auto getBool = [&](const char* key) -> bool {
            std::string k = std::string("\"") + key + "\"";
            size_t p = block.find(k); if (p == std::string::npos) return true;
            p = block.find(':', p + k.size()); if (p == std::string::npos) return true;
            p = block.find_first_not_of(" \t\r\n", p + 1);
            return p != std::string::npos && block.compare(p, 4, "true") == 0;
        };

        rule.id = getInt("id");
        rule.vk = getInt("virtualKey");
        rule.fps = getInt("targetFps");
        rule.enabled = getBool("enabled");

        if (rule.id > 0 && rule.vk > 0 && rule.vk <= 255 && rule.fps >= 0) {
            g_rules.push_back(rule);
            if (rule.id >= g_nextId) g_nextId = rule.id + 1;
        }
        pos = braceEnd + 1;
    }
    return !g_rules.empty() || (restorePos != std::string::npos && g_restoreEnabled);
}

static void SaveRulesToFile(const wchar_t* path) {
    std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
    std::string json = "{\n  \"restoreHotkey\": {\"virtualKey\": " + std::to_string(g_restoreKey) +
        ", \"enabled\": " + (g_restoreEnabled ? "true" : "false") + "},\n  \"rules\": [\n";
    for (size_t i = 0; i < g_rules.size(); ++i) {
        const FpsRule& r = g_rules[i];
        json += "    {\n";
        json += "      \"id\": " + std::to_string(r.id) + ",\n";
        json += "      \"virtualKey\": " + std::to_string(r.vk) + ",\n";
        json += "      \"targetFps\": " + std::to_string(r.fps) + ",\n";
        json += "      \"enabled\": " + std::string(r.enabled ? "true" : "false") + "\n";
        json += "    }";
        if (i < g_rules.size() - 1) json += ",";
        json += "\n";
    }
    json += "  ]\n}\n";

    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD w;
        WriteFile(h, json.data(), (DWORD)json.size(), &w, nullptr);
        CloseHandle(h);
    }
}

static void LoadDefaultRules() {
    std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
    g_rules.clear();
    g_rules.push_back({1, VK_F1, 60, true});
    g_rules.push_back({2, VK_F2, 120, true});
    g_rules.push_back({3, VK_F3, 0, true});   // unlimited
    g_rules.push_back({4, VK_F4, 30, true});
    g_nextId = 5;
    g_restoreKey = VK_F9;
    g_restoreEnabled = true;
}

// ============================================================
// Hotkey handler
// ============================================================
static void HandleHotkeyPress(int vk) {
    if (g_restoreEnabled && vk == g_restoreKey) {
        int res = FpsCore::RestoreFpsInternal();
        LOG_EXT("[FpsHotkeyExt] VK=%d -> restore startup FPS (res=%d)", vk, res);
        return;
    }
    for (size_t i = 0; i < g_rules.size(); ++i) {
        if (g_rules[i].vk == vk && g_rules[i].enabled) {
            int res = FpsCore::SetFpsInternal(g_rules[i].fps);
            LOG_EXT("[FpsHotkeyExt] VK=%d -> FPS=%d (res=%d)", vk, g_rules[i].fps, res);
            break;
        }
    }
}

static DWORD WINAPI MonitorProc(LPVOID) {
    // Keep hooks and worker code loaded for the process lifetime. Reinjection does
    // not reload a DLL; apply upgrades by restarting the game.
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(g_module), &pinned)) return 0;
    wchar_t path[32768]{};
    GetModuleFileNameW(g_module, path, _countof(path));
    const std::wstring dllPath(path);
    const size_t slash = dllPath.find_last_of(L"\\/");
    {
        std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
        g_configPath = dllPath.substr(0, slash + 1) + L"fps_hotkeys.json";
    }
    GetModuleFileNameW(nullptr, path, _countof(path));
    const std::wstring gamePath(path);
    {
        std::lock_guard<std::mutex> lock(LogExt::g_logMutex);
        LogExt::g_logPath = gamePath.substr(0, gamePath.find_last_of(L"\\/") + 1) + L"fps_hotkey_ext.log";
    }
    LOG_EXT("[FpsHotkeyExt] Attached v2.3; config=%ls", g_configPath.c_str());
    if (!MacroBridge::Init()) LOG_EXT("[FpsHotkeyExt] Macro bridge unavailable: %lu", GetLastError());
    if (!LoadRulesFromFile(g_configPath.c_str())) LoadDefaultRules();
    g_monStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_monStopEvent) { LOG_EXT("[FpsHotkeyExt] CreateEvent failed: %lu", GetLastError()); return 0; }
    {
        std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
        LOG_EXT("[FpsHotkeyExt] Monitor started (%zu rules)", g_rules.size());
    }
    bool wasDown[256]{};
    ULONGLONG nextInit = 0, nextWrite = 0;
    while (WaitForSingleObject(g_monStopEvent, 10) == WAIT_TIMEOUT) {
        const ULONGLONG now = GetTickCount64();
        if (!FpsCore::g_initialized.load() && now >= nextInit) {
            FpsCore::Init(FpsCoreLogger);
            nextInit = GetTickCount64() + 5000;
        }
        MacroBridge::Poll();
        if (now >= nextWrite) { FpsCore::Tick(); nextWrite = now + 500; }
        HWND foreground = GetForegroundWindow();
        DWORD pid = 0;
        if (foreground) GetWindowThreadProcessId(foreground, &pid);
        std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
        bool checked[256]{};
        if (g_restoreEnabled) {
            const bool down = (GetAsyncKeyState(g_restoreKey) & 0x8000) != 0;
            if (pid == GetCurrentProcessId() && down && !wasDown[g_restoreKey]) HandleHotkeyPress(g_restoreKey);
            wasDown[g_restoreKey] = down;
            checked[g_restoreKey] = true;
        }
        for (size_t i = 0; i < g_rules.size(); ++i) {
            const int vk = g_rules[i].vk;
            if (!g_rules[i].enabled || checked[vk]) continue;
            checked[vk] = true;
            const bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (pid == GetCurrentProcessId() && down && !wasDown[vk]) HandleHotkeyPress(vk);
            wasDown[vk] = down;
        }
    }
    LOG_EXT("[FpsHotkeyExt] Monitor stopped");
    return 0;
}

// ============================================================
// Exported API
// ============================================================
extern "C" {
    __declspec(dllexport) bool AddHotkeyRule(int vk, int fps) {
        if (vk <= 0 || vk > 255 || fps < 0) return false;
        std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
        FpsRule r{ g_nextId++, vk, fps, true };
        g_rules.push_back(r);
        LOG_EXT("[FpsHotkeyExt] Added rule: VK=%d FPS=%d (id=%d)", vk, fps, r.id);
        if (!g_configPath.empty()) SaveRulesToFile(g_configPath.c_str());
        return true;
    }

    __declspec(dllexport) bool RemoveHotkeyRule(int id) {
        std::lock_guard<std::recursive_mutex> lock(g_rulesMutex);
        for (size_t i = 0; i < g_rules.size(); ++i) {
            if (g_rules[i].id == id) {
                g_rules.erase(g_rules.begin() + i);
                LOG_EXT("[FpsHotkeyExt] Removed rule id=%d", id);
                if (!g_configPath.empty()) SaveRulesToFile(g_configPath.c_str());
                return true;
            }
        }
        return false;
    }

    __declspec(dllexport) int GetActiveFps() {
        return FpsCore::GetFpsInternal();
    }

    __declspec(dllexport) int ForceSetFps(int fps) {
        return FpsCore::SetFpsInternal(fps);
    }

    __declspec(dllexport) int RestoreFps() {
        return FpsCore::RestoreFpsInternal();
    }
}

// ============================================================
// DLL Entry Point
// ============================================================
extern "C" BOOL WINAPI DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = hModule;
        DisableThreadLibraryCalls(hModule);
        // No scanning, file I/O, hook installation or thread waits under loader lock.
        HANDLE worker = CreateThread(nullptr, 0, MonitorProc, nullptr, 0, nullptr);
        if (!worker) return FALSE;
        CloseHandle(worker);
    }
    return TRUE;
}
