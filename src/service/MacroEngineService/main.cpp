#include "DriverClient.h"

#include <iostream>
#include <thread>
#include <vector>

int wmain()
{
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);

    DriverClient client;
    if (!client.OpenFirstDevice())
    {
        std::wcerr << L"MacroHID device not found. Install the test-signed driver first. error="
                   << client.LastErrorCode() << L"\n";
        return 2;
    }

    std::wcout << L"Opened " << client.DevicePath() << L"\n";

    if (!client.Ping())
    {
        std::wcerr << L"Driver ping failed. error=" << client.LastErrorCode() << L"\n";
        return 3;
    }

    const std::vector<unsigned char> ctrlA =
    {
        MACROHID_KEYBOARD_REPORT_ID,
        0x01,
        0x00,
        0x04,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00
    };

    const std::vector<unsigned char> release =
    {
        MACROHID_KEYBOARD_REPORT_ID,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00
    };

    if (!client.SubmitReport(1, ctrlA))
    {
        std::wcerr << L"Submit key-down report failed. error=" << client.LastErrorCode() << L"\n";
        return 4;
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(5));

    if (!client.SubmitReport(2, release))
    {
        std::wcerr << L"Submit release report failed. error=" << client.LastErrorCode() << L"\n";
        return 5;
    }

    auto stats = client.GetStats();
    if (stats.has_value())
    {
        std::wcout << L"reportsSubmitted=" << stats->ReportsSubmitted
                   << L" reportsRejected=" << stats->ReportsRejected
                   << L" lastStatus=0x" << std::hex << stats->LastNtStatus << std::dec << L"\n";
    }

    return 0;
}
