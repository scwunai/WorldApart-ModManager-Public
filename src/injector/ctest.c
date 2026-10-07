// pinpoint CreateProcess err=87 cause
#include <windows.h>
#include <stdio.h>

static void Try(const wchar_t *label, const wchar_t *cmd, const wchar_t *dir)
{
    STARTUPINFOW si = {0};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {0};
    if (CreateProcessW(NULL, (LPWSTR)cmd, NULL, NULL, FALSE, 0, NULL, dir, &si, &pi)) {
        wprintf(L"%-28s OK pid=%lu\n", label, pi.dwProcessId);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    } else {
        wprintf(L"%-28s FAIL err=%lu\n", label, GetLastError());
    }
}

int wmain(void)
{
    Try(L"notepad trailing space", L"\"C:\\WINDOWS\\System32\\notepad.exe\" ", L"C:\\");
    Try(L"notepad clean", L"\"C:\\WINDOWS\\System32\\notepad.exe\"", L"C:\\");
    Try(L"missing file", L"\"C:\\WINDOWS\\System32\\no_such_xyz.exe\"", L"C:\\");
    // creation-flags variant as used in wastart
    {
        STARTUPINFOW si = {0};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi = {0};
        if (CreateProcessW(NULL, L"\"C:\\WINDOWS\\System32\\notepad.exe\"", NULL, NULL, FALSE,
                           PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                           PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                           NULL, L"C:\\", &si, &pi)) {
            wprintf(L"%-28s OK pid=%lu\n", L"with VM flags", pi.dwProcessId);
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        } else {
            wprintf(L"%-28s FAIL err=%lu\n", L"with VM flags", GetLastError());
        }
    }
    return 0;
}
