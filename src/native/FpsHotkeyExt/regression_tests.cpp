// Standalone synthetic engine: no game process is opened or injected.
#include "dllmain.cpp"
#include <cstdlib>

static void Check(bool passed, const char* message) {
    if (!passed) { fprintf(stderr, "FAIL: %s\n", message); exit(1); }
}

int main() {
    uint8_t code[]{0xE8, 1, 2, 3, 4, 0x90, 0xE8, 5, 6, 7, 8, 0x83, 0xF8};
    size_t offset = 0;
    Check(FpsCore::ScanPattern(code, sizeof(code), "E8 ?? ?? ?? ?? 83 F8", &offset) && offset == 6,
        "full signature and final offset; ignore unrelated E8");
    Check(FpsCore::ScanPattern(code, sizeof(code), "E8 ? ? ? ? 83 F8", &offset), "single wildcards");
    Check(!FpsCore::ScanPattern(code, 2, "E8 ? ? ? ? 83 F8", &offset), "short buffer");
    Check(!FpsCore::ScanPattern(code, sizeof(code), "E8 ? ? ? ?", &offset), "ambiguous matches");
    Check(!FpsCore::ScanPattern(code, sizeof(code), "", &offset), "empty pattern");
    Check(!FpsCore::ScanPattern(code, sizeof(code), "E8 ZZ", &offset), "invalid hex");
    Check(LoadRulesFromFile(L"fps_hotkeys.json"), "load supplied configuration");
    Check(g_rules.size() == 8, "eight supplied rules");
    for (const auto& rule : g_rules) Check(rule.enabled, "whitespace before true");
    Check(g_restoreKey == VK_F9 && g_restoreEnabled, "configured restore hotkey");
    wchar_t tempDir[MAX_PATH]{}, tempFile[MAX_PATH]{};
    Check(GetTempPathW(MAX_PATH, tempDir) != 0 && GetTempFileNameW(tempDir, L"fps", 0, tempFile) != 0,
        "create config roundtrip fixture");
    g_restoreKey = VK_F10;
    g_restoreEnabled = false;
    SaveRulesToFile(tempFile);
    g_restoreKey = VK_F9;
    g_restoreEnabled = true;
    Check(LoadRulesFromFile(tempFile) && g_restoreKey == VK_F10 && !g_restoreEnabled,
        "persist custom restore key and disabled state");
    DeleteFileW(tempFile);
    Check(LoadRulesFromFile(L"fps_hotkeys.json"), "reload supplied configuration");
    Check(!AddHotkeyRule(256, 60) && !AddHotkeyRule(112, -1), "reject invalid exported arguments");

    // Match the current game's jump stub -> mov [rip+disp32],ecx; ret.
    auto memory = static_cast<uint8_t*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    Check(memory != nullptr, "allocate synthetic setter");
    memset(memory, 0xCC, 4096);
    uint8_t setterCode[]{0xE9, 0x0B, 0, 0, 0};
    uint8_t body[]{0x89, 0x0D, 0x6A, 0, 0, 0, 0xC3};
    memcpy(memory, setterCode, sizeof(setterCode));
    memcpy(memory + 16, body, sizeof(body));
    FlushInstructionCache(GetCurrentProcess(), memory, 32);
    auto setter = reinterpret_cast<FpsCore::Setter>(memory);
    auto written = reinterpret_cast<int*>(memory + 128);
    Check(MH_Initialize() == MH_OK, "initialize MinHook");
    Check(MH_CreateHook(memory, reinterpret_cast<void*>(FpsCore::DetourSetFrameCount),
        reinterpret_cast<void**>(&FpsCore::g_originalSetter)) == MH_OK, "create actual setter hook");
    Check(MH_EnableHook(memory) == MH_OK, "enable actual setter hook");
    FpsCore::g_fnSetFrameCount = setter;
    FpsCore::g_initialized.store(true);
    setter(151);
    Check(*written == 151, "preserve launcher's target before first hotkey");
    Check(FpsCore::SetFpsInternal(120) == 0 && *written == 120, "immediate dynamic target");
    setter(151);
    Check(*written == 120, "launcher overwrite intercepted");
    HandleHotkeyPress(g_restoreKey);
    Check(*written == 151 && !FpsCore::g_override.load() && GetActiveFps() == 151,
        "restore hotkey recovers initial target and releases override");
    setter(90);
    FpsCore::Tick();
    Check(*written == 90, "restore stops periodic override and allows launcher writes");
    Check(FpsCore::SetFpsInternal(0) == 0 && *written == 999, "unlimited normalized to positive value");
    setter(60);
    Check(*written == 999, "unlimited persists against overwrite");
    Check(RestoreFps() == 0 && *written == 151, "restore still uses initial target after multiple switches");
    Check(FpsCore::SetFpsInternal(0) == 0 && *written == 999, "switch again after restore");
    Check(FpsCore::SetFpsInternal(-1) == -2 && *written == 999, "reject negative fps");
    Check(FpsCore::SetFpsInternal(2000) == 0 && *written == 1000, "engine cap");
    FpsCore::Tick();
    Check(*written == 1000 && GetActiveFps() == 1000, "periodic writer and exported target");
    Check(MacroBridge::Init(), "create Macro IPC mapping");
    MacroBridge::fields[7] = 144;
    InterlockedExchange(&MacroBridge::fields[5], 1);
    MacroBridge::Poll();
    Check(*written == 144 && MacroBridge::fields[6] == 1 && MacroBridge::fields[8] == 0,
        "Macro IPC applies target and acknowledges sequence");
    MacroBridge::fields[7] = -1;
    InterlockedExchange(&MacroBridge::fields[5], 2);
    MacroBridge::Poll();
    Check(*written == 151 && !FpsCore::g_override.load() && MacroBridge::fields[6] == 2,
        "Macro IPC restore releases override");
    MacroBridge::fields[7] = -2;
    InterlockedExchange(&MacroBridge::fields[5], 3);
    MacroBridge::Poll();
    Check(MacroBridge::fields[8] == -2 && *written == 151, "IPC rejects invalid command");
    Check(MH_DisableHook(memory) == MH_OK, "disable test hook");
    Check(MH_RemoveHook(memory) == MH_OK, "remove test hook");
    MH_Uninitialize();
    VirtualFree(memory, 0, MEM_RELEASE);
    puts("PASS: scanner, supplied hotkey config, actual x64 setter hook, overwrite, unlimited, exports");
    return 0;
}
