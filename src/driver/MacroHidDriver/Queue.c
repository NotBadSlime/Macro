#include "MacroHidDriver.h"

NTSTATUS
MacroHidCreateDefaultQueue(
    _In_ WDFDEVICE Device
    )
{
    WDF_IO_QUEUE_CONFIG queueConfig;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = MacroHidEvtIoDeviceControl;

    return WdfIoQueueCreate(Device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
}

VOID
MacroHidEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    size_t information = 0;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PDEVICE_CONTEXT context = MacroHidGetDeviceContext(device);

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode)
    {
    case IOCTL_MACROHID_PING:
        status = STATUS_SUCCESS;
        break;

    case IOCTL_MACROHID_GET_STATS:
    {
        PMACROHID_DRIVER_STATS stats = NULL;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MACROHID_DRIVER_STATS), (PVOID*)&stats, NULL);
        if (NT_SUCCESS(status))
        {
            *stats = context->Stats;
            information = sizeof(MACROHID_DRIVER_STATS);
        }
        break;
    }

    case IOCTL_MACROHID_SUBMIT_REPORT:
    {
        PMACROHID_REPORT_PACKET packet = NULL;
        HID_XFER_PACKET transfer;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(MACROHID_REPORT_PACKET), (PVOID*)&packet, NULL);
        if (!NT_SUCCESS(status))
        {
            context->Stats.ReportsRejected++;
            context->Stats.LastNtStatus = status;
            break;
        }

        if (packet->Size != sizeof(MACROHID_REPORT_PACKET)
            || packet->ReportLength == 0
            || packet->ReportLength > MACROHID_MAX_REPORT_SIZE
            || packet->Report[0] != packet->ReportId)
        {
            status = STATUS_INVALID_PARAMETER;
            context->Stats.ReportsRejected++;
            context->Stats.LastNtStatus = status;
            break;
        }

        RtlZeroMemory(&transfer, sizeof(transfer));
        transfer.reportId = packet->ReportId;
        transfer.reportBuffer = packet->Report;
        transfer.reportBufferLen = packet->ReportLength;

        status = VhfReadReportSubmit(context->VhfHandle, &transfer);
        context->Stats.LastSubmitQpc = KeQueryPerformanceCounter(NULL);
        context->Stats.LastNtStatus = status;

        if (NT_SUCCESS(status))
        {
            context->Stats.ReportsSubmitted++;
            information = packet->ReportLength;
        }
        else
        {
            context->Stats.ReportsRejected++;
        }
        break;
    }

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        context->Stats.LastNtStatus = status;
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}
