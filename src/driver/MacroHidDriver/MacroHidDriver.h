#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>

#include "..\..\shared\MacroHidProtocol\MacroHidProtocol.h"

typedef struct _DEVICE_CONTEXT
{
    VHFHANDLE VhfHandle;
    MACROHID_DRIVER_STATS Stats;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, MacroHidGetDeviceContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD MacroHidEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP MacroHidEvtDeviceContextCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL MacroHidEvtIoDeviceControl;

NTSTATUS MacroHidCreateDevice(_Inout_ PWDFDEVICE_INIT DeviceInit);
NTSTATUS MacroHidCreateDefaultQueue(_In_ WDFDEVICE Device);
