// WAStart v2 - IFEO Debugger launcher/injector for WorldApart (IL2CPP + gpShell)
//
// Chain: launcher(modlauncher.exe / IFEO) -> self /do (helper)
//   -> CreateProcess WorldApart_mod.exe (hardlink, IFEO-free)
//   -> poll WorldApart* processes for UnityPlayer.dll module
//   -> remote injection of the DLL named in inject.ini
//
// v2 additions (diagnostics for E1..E4):
//   * inject target DLL path comes from inject.ini (absolute path), no recompile
//   * configurable delay after the Unity process is found (injection timing scan)
//   * target selection: unity child process or the shell process
//   * entry point selection: LoadLibraryW or a no-op GetTickCount control
//   * manual thread polling (WaitForSingleObject(th,0) + GetExitCodeThread) with
//     per-second logging, so a hung remote thread is distinguishable from a dead
//     helper; explicit OK/FAIL lines on every path
// Configuration file: <dir of wastart.exe>\inject.ini
#include <windows.h>
#include <stdio.h>
#include <tlhelp32.h>

static wchar_t g_dir[MAX_PATH] = {0};

static void Log(const wchar_t *msg)
{
    wchar_t path[MAX_PATH];
    swprintf_s(path, MAX_PATH, L"%s\\mod_inject.log", g_dir);
    FILE *f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-16LE") == 0 && f) {
        fwprintf(f, L"[%lu,+%lums] %s\n", GetCurrentProcessId(), GetTickCount(), msg);
        fclose(f);
    }
}

static void LogErr(const wchar_t *what, DWORD e)
{
    wchar_t buf[512];
    swprintf_s(buf, 512, L"FAIL: %s err=%lu", what, e);
    Log(buf);
}

typedef struct {
    wchar_t dll[MAX_PATH];
    int     delay_ms;
    int     target_mode;    // 0 = unity process (needs UnityPlayer.dll),
                            // 1 = shell process, 2 = first non-shell WorldApart*
    int     entry_tick;     // 0 = LoadLibraryW, 1 = GetTickCount (control)
    int     watch_ms;
} Cfg;

static void ReadCfg(Cfg *c)
{
    wchar_t ini[MAX_PATH];
    swprintf_s(ini, MAX_PATH, L"%s\\inject.ini", g_dir);
    c->dll[0] = 0;
    GetPrivateProfileStringW(L"inject", L"dll", L"", c->dll, MAX_PATH, ini);
    c->delay_ms    = GetPrivateProfileIntW(L"inject", L"delay_ms", 0, ini);
    c->target_mode = GetPrivateProfileIntW(L"inject", L"target_mode", 0, ini);
    c->entry_tick  = GetPrivateProfileIntW(L"inject", L"entry_tick", 0, ini);
    c->watch_ms    = GetPrivateProfileIntW(L"inject", L"watch_ms", 25000, ini);
    if (GetPrivateProfileIntW(L"inject", L"target_shell", 0, ini)) c->target_mode = 1;
}

// Find a candidate target process.
//   mode 0: WorldApart* process whose module list contains UnityPlayer.dll
//   mode 2: first WorldApart* process whose pid != shell_pid (the Unity child,
//           may not have UnityPlayer.dll loaded yet)
static DWORD FindTarget(int mode, DWORD shell_pid)
{
    DWORD found = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W pe;
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe)) {
        do {
            if (wcsnicmp(pe.szExeFile, L"WorldApart", 10) != 0) continue;
            if (mode == 2) {
                if (pe.th32ProcessID != shell_pid) { found = pe.th32ProcessID; break; }
                continue;
            }
            HANDLE msnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pe.th32ProcessID);
            if (msnap == INVALID_HANDLE_VALUE) continue;
            MODULEENTRY32W me;
            me.dwSize = sizeof(me);
            if (Module32FirstW(msnap, &me)) {
                do {
                    if (_wcsicmp(me.szModule, L"UnityPlayer.dll") == 0) {
                        found = pe.th32ProcessID;
                        break;
                    }
                } while (Module32NextW(msnap, &me));
            }
            CloseHandle(msnap);
            if (found) break;
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return found;
}

// log which interesting modules the target already has loaded
static void LogTargetModules(DWORD pid)
{
    static const wchar_t *want[] = {
        L"UnityPlayer.dll", L"GameAssembly.dll", L"gpShell.dll", L"kernel32.dll", NULL
    };
    HANDLE msnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid);
    if (msnap == INVALID_HANDLE_VALUE) { Log(L"WARN: module snapshot failed"); return; }
    wchar_t have[512] = L"";
    MODULEENTRY32W me;
    me.dwSize = sizeof(me);
    if (Module32FirstW(msnap, &me)) {
        do {
            for (int i = 0; want[i]; i++) {
                if (_wcsicmp(me.szModule, want[i]) == 0) {
                    wchar_t t[128];
                    swprintf_s(t, 128, L"%s ", want[i]);
                    lstrcatW(have, t);
                }
            }
        } while (Module32NextW(msnap, &me));
    }
    CloseHandle(msnap);
    wchar_t buf[768];
    swprintf_s(buf, 768, L"INJECT: target pid=%lu modules: %s", pid, have);
    Log(buf);
}

static int InjectDll(DWORD pid, const Cfg *cfg)
{
    wchar_t buf[768];
    swprintf_s(buf, 768, L"INJECT: begin pid=%lu dll=%s entry=%s", pid, cfg->dll,
               cfg->entry_tick ? L"GetTickCount" : L"LoadLibraryW");
    Log(buf);

    HANDLE hp = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                            PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                            FALSE, pid);
    if (!hp) { LogErr(L"OpenProcess", GetLastError()); return 2; }

    LPTHREAD_START_ROUTINE start = NULL;
    LPVOID arg = NULL;
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");

    if (cfg->entry_tick) {
        start = (LPTHREAD_START_ROUTINE)GetProcAddress(k32, "GetTickCount");
    } else {
        SIZE_T bytes = (lstrlenW(cfg->dll) + 1) * sizeof(wchar_t);
        LPVOID remote = VirtualAllocEx(hp, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!remote) { LogErr(L"VirtualAllocEx", GetLastError()); CloseHandle(hp); return 1; }
        if (!WriteProcessMemory(hp, remote, cfg->dll, bytes, NULL)) {
            LogErr(L"WriteProcessMemory", GetLastError()); CloseHandle(hp); return 1;
        }
        start = (LPTHREAD_START_ROUTINE)GetProcAddress(k32, "LoadLibraryW");
        arg = remote;
    }
    Log(L"INJECT: memory ready");

    HANDLE th = CreateRemoteThread(hp, NULL, 0, start, arg, 0, NULL);
    if (!th) {
        DWORD e = GetLastError();
        CloseHandle(hp);
        LogErr(L"CreateRemoteThread", e);
        return (e == ERROR_INVALID_PARAMETER || e == ERROR_ACCESS_DENIED) ? 2 : 1;
    }
    Log(L"INJECT: remote thread created, polling");

    DWORD waited = 0, code = STILL_ACTIVE;
    while (waited < (DWORD)cfg->watch_ms) {
        DWORD r = WaitForSingleObject(th, 1000);
        waited += 1000;
        if (r == WAIT_OBJECT_0) {
            GetExitCodeThread(th, &code);
            swprintf_s(buf, 768, L"OK: remote thread returned after %lums, exit code=0x%lx (%lu)",
                       waited, code, code);
            Log(buf);
            break;
        }
        GetExitCodeThread(th, &code);
        swprintf_s(buf, 768, L"WAIT: t=%lums thread still alive, exitcode=0x%lx (STILL_ACTIVE=%lu)",
                   waited, code, STILL_ACTIVE);
        Log(buf);
    }

    if (code == STILL_ACTIVE && waited >= (DWORD)cfg->watch_ms) {
        Log(L"FAIL: remote thread did not return within watch window (hung in target)");
        CloseHandle(th); CloseHandle(hp); return 1;
    }
    CloseHandle(th);
    CloseHandle(hp);
    if (!cfg->entry_tick && code == 0) {
        // A NULL LoadLibraryW usually means we landed on gpShell's short-lived
        // intermediate process (it is already tearing down). Treat it as a
        // retryable candidate rather than a hard failure, otherwise the real
        // Unity child is never injected.
        Log(L"RETRY: LoadLibraryW returned NULL in target (candidate likely transient)");
        return 2;
    }
    swprintf_s(buf, 768, L"DONE: injection call completed, LoadLibraryW result=0x%lx", code);
    Log(buf);
    return 0;
}

// rewrite first ".exe" in the command line to "_mod.exe"
static void RewriteToHardlink(wchar_t *cmd)
{
    int klen = lstrlenW(cmd);
    if (klen > 512) klen = 512;
    for (int i = 0; i + 4 <= klen; i++) {
        wchar_t c0 = cmd[i], c1 = cmd[i+1], c2 = cmd[i+2], c3 = cmd[i+3];
        if ((c0 == L'.') && (c1 == L'e' || c1 == L'E') &&
            (c2 == L'x' || c2 == L'X') && (c3 == L'e' || c3 == L'E')) {
            wchar_t tail[32768];
            lstrcpyW(tail, cmd + i);
            lstrcpyW(cmd + i, L"_mod");
            lstrcatW(cmd + i, tail);
            return;
        }
    }
}

static int HelperMain(const wchar_t *cmdline)
{
    Cfg cfg;
    ReadCfg(&cfg);
    {
        wchar_t buf[768];
        swprintf_s(buf, 768, L"HELPER: start dll=%s delay=%dms target_mode=%d entry=%s watch=%dms",
                   cfg.dll, cfg.delay_ms, cfg.target_mode,
                   cfg.entry_tick ? L"tick" : L"loadlib", cfg.watch_ms);
        Log(buf);
    }
    if (!cfg.dll[0]) { Log(L"FAIL: inject.ini has no dll= entry"); return 1; }

    wchar_t cmd[32768];
    lstrcpyW(cmd, cmdline);
    RewriteToHardlink(cmd);

    STARTUPINFOW si = {0};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {0};
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, 0, NULL, g_dir, &si, &pi)) {
        wchar_t buf[512];
        swprintf_s(buf, 512, L"FAIL: CreateProcess err=%lu cmd=%s", GetLastError(), cmd);
        Log(buf);
        return 1;
    }
    {
        wchar_t buf[256];
        swprintf_s(buf, 256, L"OK: created shell pid=%lu", pi.dwProcessId);
        Log(buf);
    }

    DWORD target = 0;
    int rc = 1;
    if (cfg.target_mode == 1) {
        target = pi.dwProcessId;
        Log(L"INJECT: target = shell process (config)");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        LogTargetModules(target);
        return InjectDll(target, &cfg);
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    // gpShell spawns short-lived intermediate processes before the real Unity
    // child; a candidate can die between Toolhelp enumeration and OpenProcess
    // (measured: OpenProcess err=87). So retry with a fresh candidate.
    DWORD deadline = GetTickCount() + 20000;
    while (GetTickCount() < deadline) {
        target = FindTarget(cfg.target_mode, pi.dwProcessId);
        if (!target) { Sleep(5); continue; }
        {
            wchar_t buf[192];
            swprintf_s(buf, 192, L"INJECT: candidate pid=%lu (mode=%d)", target, cfg.target_mode);
            Log(buf);
        }
        if (cfg.delay_ms > 0) {
            wchar_t buf[128];
            swprintf_s(buf, 128, L"INJECT: sleeping %dms before injection", cfg.delay_ms);
            Log(buf);
            Sleep(cfg.delay_ms);
        }
        LogTargetModules(target);
        rc = InjectDll(target, &cfg);
        if (rc != 2) return rc;   /* 0 = injected, 1 = hard failure */
        Log(L"RETRY: candidate vanished before injection, looking for another");
        Sleep(20);
    }
    Log(L"FAIL: no injectable WorldApart child process found within 20 s");
    return 1;
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, LPWSTR lpCmdLine, int nShow)
{
    (void)hInst; (void)hPrev; (void)nShow;

    wchar_t self[MAX_PATH];
    GetModuleFileNameW(NULL, self, MAX_PATH);
    wchar_t selffull[MAX_PATH];
    lstrcpyW(selffull, self);
    wchar_t *slash = wcsrchr(self, L'\\');
    if (slash) { *slash = 0; lstrcpyW(g_dir, self); }

    if (lpCmdLine[0] == L'/' &&
        (lpCmdLine[1] == L'd' || lpCmdLine[1] == L'D') &&
        (lpCmdLine[2] == L'o' || lpCmdLine[2] == L'O') &&
        lpCmdLine[3] == L' ') {
        return HelperMain(lpCmdLine + 4);
    }

    if (lpCmdLine[0] == 0) {
        Log(L"LAUNCHER: no target exe given; launch via 启动MOD版.bat or Steam launch options");
        return 1;
    }

    wchar_t helper[32768];
    swprintf_s(helper, 32768, L"\"%s\" /do %s", selffull, lpCmdLine);
    Log(L"LAUNCHER: spawning helper");

    STARTUPINFOW si = {0};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {0};
    if (!CreateProcessW(NULL, helper, NULL, NULL, FALSE, 0, NULL, g_dir, &si, &pi)) {
        LogErr(L"spawn helper", GetLastError());
        return 1;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
