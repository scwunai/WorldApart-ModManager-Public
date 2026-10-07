// kimi_probe.dll - in-process diagnostic probe
// DllMain writes marker lines to <game dir>\kimi_probe.txt:
//  - that the probe loaded (remote injection works at all)
//  - the result of loading doorstop's winhttp.dll in-process WITH
//    GetLastError, so we learn the real failure reason
// Then returns TRUE (harmless).
#include <windows.h>
#include <stdio.h>

static wchar_t g_dir[MAX_PATH] = {0};

static void Mark(const wchar_t *msg)
{
    wchar_t path[MAX_PATH];
    swprintf_s(path, MAX_PATH, L"%s\\kimi_probe.txt", g_dir);
    FILE *f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-8") == 0 && f) {
        fwprintf(f, L"%s\n", msg);
        fclose(f);
    }
}

static void MarkErr(const wchar_t *what, DWORD e)
{
    wchar_t buf[256];
    swprintf_s(buf, 256, L"%s err=%lu", what, e);
    Mark(buf);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    wchar_t self[MAX_PATH];
    DWORD n = GetModuleFileNameW(hModule, self, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        wchar_t *slash = wcsrchr(self, L'\\');
        if (slash) { *slash = 0; lstrcpyW(g_dir, self); }
    }
    Mark(L"PROBE: DLL_PROCESS_ATTACH");

    wchar_t door[MAX_PATH];
    swprintf_s(door, MAX_PATH, L"%s\\kimi_door.dll", g_dir);
    SetLastError(0);
    HMODULE h = LoadLibraryW(door);
    if (h) {
        Mark(L"PROBE: doorstop load OK");
    } else {
        MarkErr(L"PROBE: doorstop load FAILED", GetLastError());
    }
    return TRUE;
}
