#include "MacroHidDriver.h"
#include "HidReportDescriptor.h"

NTSTATUS
MacroHidCreateDevice(
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    PDEVICE_CONTEXT context;
    VHF_CONFIG vhfConfig;

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = MacroHidEvtDeviceContextCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_MACROHID, NULL);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    context = MacroHidGetDeviceContext(device);
    RtlZeroMemory(context, sizeof(*context));
    context->Stats.ProtocolVersion = MACROHID_PROTOCOL_VERSION;

    VHF_CONFIG_INIT(&vhfConfig, WdfDeviceWdmGetDeviceObject(device), sizeof(g_MacroHidReportDescriptor), (PUCHAR)g_MacroHidReportDescriptor);

    status = VhfCreate(&vhfConfig, &context->VhfHandle);
    if (!NT_SUCCESS(status))
    {
        context->Stats.LastNtStatus = status;
        return status;
    }

    status = VhfStart(context->VhfHandle);
    if (!NT_SUCCESS(status))
    {
        context->Stats.LastNtStatus = status;
        return status;
    }

    return MacroHidCreateDefaultQueue(device);
}

VOID
MacroHidEvtDeviceContextCleanup(
    _In_ WDFOBJECT Object
    )
{
    PDEVICE_CONTEXT context = MacroHidGetDeviceContext((WDFDEVICE)Object);

    if (context->VhfHandle != NULL)
    {
        VhfDelete(context->VhfHandle, TRUE);
        context->VhfHandle = NULL;
    }
}
