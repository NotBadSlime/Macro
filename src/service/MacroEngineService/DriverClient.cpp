#include "DriverClient.h"

#include <initguid.h>
#include <setupapi.h>
#include <stdexcept>

#pragma comment(lib, "setupapi.lib")

DriverClient::DriverClient()
    : handle_(INVALID_HANDLE_VALUE),
      lastError_(ERROR_SUCCESS)
{
}

DriverClient::~DriverClient()
{
    if (handle_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(handle_);
    }
}

bool DriverClient::OpenFirstDevice()
{
    auto path = FindFirstDevicePath();
    if (!path.has_value())
    {
        lastError_ = ERROR_NOT_FOUND;
        return false;
    }

    HANDLE handle = CreateFileW(
        path->c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (handle == INVALID_HANDLE_VALUE)
    {
        lastError_ = GetLastError();
        return false;
    }

    if (handle_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(handle_);
    }

    handle_ = handle;
    devicePath_ = *path;
    lastError_ = ERROR_SUCCESS;
    return true;
}

bool DriverClient::Ping() const
{
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        handle_,
        static_cast<DWORD>(IOCTL_MACROHID_PING),
        nullptr,
        0,
        nullptr,
        0,
        &bytesReturned,
        nullptr);

    lastError_ = ok ? ERROR_SUCCESS : GetLastError();
    return ok != FALSE;
}

bool DriverClient::SubmitReport(unsigned long sequence, const std::vector<unsigned char>& report) const
{
    if (report.empty() || report.size() > MACROHID_MAX_REPORT_SIZE)
    {
        lastError_ = ERROR_INVALID_PARAMETER;
        return false;
    }

    MACROHID_REPORT_PACKET packet = {};
    packet.Size = sizeof(packet);
    packet.Sequence = sequence;
    QueryPerformanceCounter(&packet.HostQpc);
    packet.ReportId = report[0];
    packet.ReportLength = static_cast<UCHAR>(report.size());
    memcpy(packet.Report, report.data(), report.size());

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        handle_,
        static_cast<DWORD>(IOCTL_MACROHID_SUBMIT_REPORT),
        &packet,
        sizeof(packet),
        nullptr,
        0,
        &bytesReturned,
        nullptr);

    lastError_ = ok ? ERROR_SUCCESS : GetLastError();
    return ok != FALSE;
}

std::optional<MACROHID_DRIVER_STATS> DriverClient::GetStats() const
{
    MACROHID_DRIVER_STATS stats = {};
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        handle_,
        static_cast<DWORD>(IOCTL_MACROHID_GET_STATS),
        nullptr,
        0,
        &stats,
        sizeof(stats),
        &bytesReturned,
        nullptr);

    lastError_ = ok ? ERROR_SUCCESS : GetLastError();
    if (!ok || bytesReturned != sizeof(stats))
    {
        return std::nullopt;
    }

    return stats;
}

const std::wstring& DriverClient::DevicePath() const noexcept
{
    return devicePath_;
}

unsigned long DriverClient::LastErrorCode() const noexcept
{
    return lastError_;
}

std::optional<std::wstring> DriverClient::FindFirstDevicePath()
{
    HDEVINFO deviceInfo = SetupDiGetClassDevsW(
        &GUID_DEVINTERFACE_MACROHID,
        nullptr,
        nullptr,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

    if (deviceInfo == INVALID_HANDLE_VALUE)
    {
        return std::nullopt;
    }

    SP_DEVICE_INTERFACE_DATA interfaceData = {};
    interfaceData.cbSize = sizeof(interfaceData);

    if (!SetupDiEnumDeviceInterfaces(deviceInfo, nullptr, &GUID_DEVINTERFACE_MACROHID, 0, &interfaceData))
    {
        SetupDiDestroyDeviceInfoList(deviceInfo);
        return std::nullopt;
    }

    DWORD requiredSize = 0;
    SetupDiGetDeviceInterfaceDetailW(deviceInfo, &interfaceData, nullptr, 0, &requiredSize, nullptr);
    if (requiredSize == 0)
    {
        SetupDiDestroyDeviceInfoList(deviceInfo);
        return std::nullopt;
    }

    std::vector<unsigned char> buffer(requiredSize);
    auto* detail = reinterpret_cast<PSP_DEVICE_INTERFACE_DETAIL_DATA_W>(buffer.data());
    detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);

    if (!SetupDiGetDeviceInterfaceDetailW(deviceInfo, &interfaceData, detail, requiredSize, nullptr, nullptr))
    {
        SetupDiDestroyDeviceInfoList(deviceInfo);
        return std::nullopt;
    }

    std::wstring path(detail->DevicePath);
    SetupDiDestroyDeviceInfoList(deviceInfo);
    return path;
}
