#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <malloc.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#include <common.h>
#include <types.h>
#include "libbfio_definitions.h"
#include "libbfio_handle.h"
#include "libfsapfs_definitions.h"
#include "libfsapfs_container.h"
#include "libcerror_error.h"

typedef struct apfsmount_raw_io
{
    wchar_t *path;
    HANDLE handle;
    uint64_t base_offset;
    uint64_t size;
    uint64_t position;
    DWORD sector_size;
} apfsmount_raw_io_t;

typedef struct apfsmount_raw_context
{
    libfsapfs_container_t *container;
    libbfio_handle_t *file_io_handle;
} apfsmount_raw_context_t;

static volatile LONG apfsmount_last_stage = 0;
static volatile LONG apfsmount_last_win32_error = 0;
static char apfsmount_last_error[16384];

static int apfsmount_raw_free(intptr_t **io_handle, libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = NULL;
    (void) error;

    if (io_handle == NULL || *io_handle == NULL)
        return 1;

    raw = (apfsmount_raw_io_t *) *io_handle;
    if (raw->handle != NULL && raw->handle != INVALID_HANDLE_VALUE)
        CloseHandle(raw->handle);
    free(raw->path);
    free(raw);
    *io_handle = NULL;
    return 1;
}

static int apfsmount_raw_clone(
    intptr_t **destination_io_handle,
    intptr_t *source_io_handle,
    libcerror_error_t **error)
{
    apfsmount_raw_io_t *source = (apfsmount_raw_io_t *) source_io_handle;
    apfsmount_raw_io_t *destination = NULL;
    size_t path_length = 0;
    (void) error;

    if (destination_io_handle == NULL || source == NULL)
        return -1;

    destination = (apfsmount_raw_io_t *) calloc(1, sizeof(apfsmount_raw_io_t));
    if (destination == NULL)
        return -1;

    path_length = wcslen(source->path) + 1;
    destination->path = (wchar_t *) malloc(path_length * sizeof(wchar_t));
    if (destination->path == NULL)
    {
        free(destination);
        return -1;
    }
    memcpy(destination->path, source->path, path_length * sizeof(wchar_t));
    destination->handle = INVALID_HANDLE_VALUE;
    destination->base_offset = source->base_offset;
    destination->size = source->size;
    destination->position = source->position;
    *destination_io_handle = (intptr_t *) destination;
    return 1;
}

static int apfsmount_raw_open(intptr_t *io_handle, int access_flags, libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    (void) error;

    if (raw == NULL || (access_flags & LIBBFIO_ACCESS_FLAG_WRITE) != 0)
        return -1;
    if (raw->handle != NULL && raw->handle != INVALID_HANDLE_VALUE)
        return 1;

    raw->handle = CreateFileW(
        raw->path,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_RANDOM_ACCESS,
        NULL);
    if (raw->handle == INVALID_HANDLE_VALUE)
    {
        InterlockedExchange(&apfsmount_last_stage, 10);
        InterlockedExchange(&apfsmount_last_win32_error, (LONG) GetLastError());
        return -1;
    }
    raw->position = 0;
    DISK_GEOMETRY geometry;
    DWORD returned = 0;
    if (!DeviceIoControl(raw->handle, IOCTL_DISK_GET_DRIVE_GEOMETRY,
            NULL, 0, &geometry, sizeof(geometry), &returned, NULL) ||
            geometry.BytesPerSector == 0 || geometry.BytesPerSector > 65536 ||
            (geometry.BytesPerSector & (geometry.BytesPerSector - 1)) != 0)
    {
        InterlockedExchange(&apfsmount_last_stage, 15);
        InterlockedExchange(&apfsmount_last_win32_error, (LONG) GetLastError());
        CloseHandle(raw->handle);
        raw->handle = INVALID_HANDLE_VALUE;
        return -1;
    }
    raw->sector_size = geometry.BytesPerSector;
    return 1;
}

static int apfsmount_raw_close(intptr_t *io_handle, libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    (void) error;

    if (raw != NULL && raw->handle != NULL && raw->handle != INVALID_HANDLE_VALUE)
    {
        CloseHandle(raw->handle);
        raw->handle = INVALID_HANDLE_VALUE;
    }
    return 0;
}

static ssize_t apfsmount_raw_read(
    intptr_t *io_handle,
    uint8_t *buffer,
    size_t size,
    libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    LARGE_INTEGER absolute_offset;
    DWORD bytes_to_read = 0;
    DWORD bytes_read = 0;
    uint64_t remaining = 0;
    uint64_t absolute;
    DWORD prefix;
    uint8_t *aligned;
    (void) error;

    if (raw == NULL || buffer == NULL || raw->handle == INVALID_HANDLE_VALUE)
        return -1;
    if (raw->position >= raw->size || size == 0)
        return 0;

    remaining = raw->size - raw->position;
    if ((uint64_t) size > remaining)
        size = (size_t) remaining;
    if (raw->sector_size == 0 || raw->base_offset > INT64_MAX - raw->position)
        return -1;
    if (size > 1024 * 1024) size = 1024 * 1024;
    absolute = raw->base_offset + raw->position;
    prefix = (DWORD) (absolute % raw->sector_size);
    bytes_to_read = (DWORD) (((prefix + size + raw->sector_size - 1) / raw->sector_size) * raw->sector_size);
    absolute_offset.QuadPart = (LONGLONG) (absolute - prefix);
    aligned = (uint8_t *) _aligned_malloc(bytes_to_read, raw->sector_size);
    if (aligned == NULL) return -1;
    if (!SetFilePointerEx(raw->handle, absolute_offset, NULL, FILE_BEGIN))
    {
        InterlockedExchange(&apfsmount_last_stage, 12);
        InterlockedExchange(&apfsmount_last_win32_error, (LONG) GetLastError());
        _aligned_free(aligned);
        return -1;
    }
    if (!ReadFile(raw->handle, aligned, bytes_to_read, &bytes_read, NULL))
    {
        InterlockedExchange(&apfsmount_last_stage, 13);
        InterlockedExchange(&apfsmount_last_win32_error, (LONG) GetLastError());
        _aligned_free(aligned);
        return -1;
    }
    bytes_read = bytes_read > prefix ? bytes_read - prefix : 0;
    if (bytes_read > size) bytes_read = (DWORD) size;
    memcpy(buffer, aligned + prefix, bytes_read);
    _aligned_free(aligned);
    raw->position += bytes_read;
    return (ssize_t) bytes_read;
}

static ssize_t apfsmount_raw_write(
    intptr_t *io_handle,
    const uint8_t *buffer,
    size_t size,
    libcerror_error_t **error)
{
    (void) io_handle;
    (void) buffer;
    (void) size;
    (void) error;
    return -1;
}

static off64_t apfsmount_raw_seek(
    intptr_t *io_handle,
    off64_t offset,
    int whence,
    libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    int64_t origin = 0;
    int64_t target = 0;
    (void) error;

    if (raw == NULL)
        return -1;
    if (whence == SEEK_CUR)
        origin = (int64_t) raw->position;
    else if (whence == SEEK_END)
        origin = (int64_t) raw->size;
    else if (whence != SEEK_SET)
        return -1;

    if (offset > 0 && origin > INT64_MAX - offset)
        return -1;
    target = origin + offset;
    if (target < 0 || (uint64_t) target > raw->size)
    {
        InterlockedExchange(&apfsmount_last_stage, 14);
        return -1;
    }
    raw->position = (uint64_t) target;
    return (off64_t) target;
}

static int apfsmount_raw_exists(intptr_t *io_handle, libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    DWORD attributes;
    (void) error;

    if (raw == NULL)
        return -1;
    attributes = GetFileAttributesW(raw->path);
    if (attributes == INVALID_FILE_ATTRIBUTES)
    {
        InterlockedExchange(&apfsmount_last_stage, 11);
        InterlockedExchange(&apfsmount_last_win32_error, (LONG) GetLastError());
    }
    return attributes == INVALID_FILE_ATTRIBUTES ? 0 : 1;
}

static int apfsmount_raw_is_open(intptr_t *io_handle, libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    (void) error;
    return raw != NULL && raw->handle != NULL && raw->handle != INVALID_HANDLE_VALUE ? 1 : 0;
}

static int apfsmount_raw_get_size(
    intptr_t *io_handle,
    size64_t *size,
    libcerror_error_t **error)
{
    apfsmount_raw_io_t *raw = (apfsmount_raw_io_t *) io_handle;
    (void) error;

    if (raw == NULL || size == NULL)
        return -1;
    *size = (size64_t) raw->size;
    return 1;
}

__declspec(dllexport) int __cdecl apfsmount_open_raw(
    const wchar_t *path,
    int64_t base_offset,
    uint64_t size,
    void **context_value,
    void **container_value)
{
    apfsmount_raw_context_t *context = NULL;
    apfsmount_raw_io_t *raw = NULL;
    size_t path_length = 0;

    if (path == NULL || base_offset < 0 || size == 0 ||
        context_value == NULL || container_value == NULL)
        return -1;
    InterlockedExchange(&apfsmount_last_stage, 1);
    apfsmount_last_error[0] = 0;
    InterlockedExchange(&apfsmount_last_win32_error, 0);
    *context_value = NULL;
    *container_value = NULL;

    context = (apfsmount_raw_context_t *) calloc(1, sizeof(apfsmount_raw_context_t));
    raw = (apfsmount_raw_io_t *) calloc(1, sizeof(apfsmount_raw_io_t));
    if (context == NULL || raw == NULL)
        goto on_error;

    path_length = wcslen(path) + 1;
    raw->path = (wchar_t *) malloc(path_length * sizeof(wchar_t));
    if (raw->path == NULL)
        goto on_error;
    memcpy(raw->path, path, path_length * sizeof(wchar_t));
    raw->handle = INVALID_HANDLE_VALUE;
    raw->base_offset = (uint64_t) base_offset;
    raw->size = size;

    if (libbfio_handle_initialize(
            &context->file_io_handle,
            (intptr_t *) raw,
            apfsmount_raw_free,
            apfsmount_raw_clone,
            apfsmount_raw_open,
            apfsmount_raw_close,
            apfsmount_raw_read,
            apfsmount_raw_write,
            apfsmount_raw_seek,
            apfsmount_raw_exists,
            apfsmount_raw_is_open,
            apfsmount_raw_get_size,
            LIBBFIO_FLAG_IO_HANDLE_MANAGED | LIBBFIO_FLAG_IO_HANDLE_CLONE_BY_FUNCTION,
            NULL) != 1)
    {
        InterlockedExchange(&apfsmount_last_stage, 3);
        goto on_error;
    }
    raw = NULL;

    if (libfsapfs_container_initialize(&context->container, NULL) != 1)
    {
        InterlockedExchange(&apfsmount_last_stage, 4);
        goto on_error;
    }
    libcerror_error_t *open_error = NULL;
    if (libfsapfs_container_open_file_io_handle(
            context->container,
            context->file_io_handle,
            LIBFSAPFS_OPEN_READ,
            &open_error) != 1)
    {
        if (open_error != NULL) {
            libcerror_error_backtrace_sprint(open_error, apfsmount_last_error, sizeof(apfsmount_last_error));
            libcerror_error_free(&open_error);
        }
        if (InterlockedCompareExchange(&apfsmount_last_stage, 0, 0) < 10)
            InterlockedExchange(&apfsmount_last_stage, 5);
        goto on_error;
    }

    *context_value = context;
    *container_value = context->container;
    return 1;

on_error:
    if (context != NULL)
    {
        if (context->container != NULL)
        {
            libfsapfs_container_close(context->container, NULL);
            libfsapfs_container_free(&context->container, NULL);
        }
        if (context->file_io_handle != NULL)
            libbfio_handle_free(&context->file_io_handle, NULL);
        free(context);
    }
    if (raw != NULL)
    {
        intptr_t *raw_value = (intptr_t *) raw;
        apfsmount_raw_free(&raw_value, NULL);
    }
    return -1;
}

__declspec(dllexport) int __cdecl apfsmount_get_last_stage(void)
{
    return (int) InterlockedCompareExchange(&apfsmount_last_stage, 0, 0);
}

__declspec(dllexport) const char * __cdecl apfsmount_get_last_error(void)
{
    return apfsmount_last_error;
}

__declspec(dllexport) int __cdecl apfsmount_get_last_win32_error(void)
{
    return (int) InterlockedCompareExchange(&apfsmount_last_win32_error, 0, 0);
}

__declspec(dllexport) int __cdecl apfsmount_close_raw(void **context_value)
{
    apfsmount_raw_context_t *context = NULL;
    int result = 1;

    if (context_value == NULL || *context_value == NULL)
        return 1;
    context = (apfsmount_raw_context_t *) *context_value;

    if (context->container != NULL)
    {
        if (libfsapfs_container_close(context->container, NULL) != 0)
            result = -1;
        if (libfsapfs_container_free(&context->container, NULL) != 1)
            result = -1;
    }
    if (context->file_io_handle != NULL &&
        libbfio_handle_free(&context->file_io_handle, NULL) != 1)
        result = -1;
    free(context);
    *context_value = NULL;
    return result;
}
