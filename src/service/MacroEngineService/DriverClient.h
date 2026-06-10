#pragma once

#include <optional>
#include <string>
#include <vector>
#include <windows.h>

#include "..\..\shared\MacroHidProtocol\MacroHidProtocol.h"

class DriverClient
{
public:
    DriverClient();
    ~DriverClient();

    DriverClient(const DriverClient&) = delete;
    DriverClient& operator=(const DriverClient&) = delete;

    bool OpenFirstDevice();
    bool Ping() const;
    bool SubmitReport(unsigned long sequence, const std::vector<unsigned char>& report) const;
    std::optional<MACROHID_DRIVER_STATS> GetStats() const;

    const std::wstring& DevicePath() const noexcept;
    unsigned long LastErrorCode() const noexcept;

private:
    HANDLE handle_;
    std::wstring devicePath_;
    mutable unsigned long lastError_;

    static std::optional<std::wstring> FindFirstDevicePath();
};
