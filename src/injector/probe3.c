// probe3.c - WorldApart IL2CPP bootstrap injector
//
// WHY THIS EXISTS (measured, see KIMI\实施报告.md):
//   doorstop 4.3 activates by IAT-hooking kernel32!GetProcAddress inside
//   UnityPlayer.dll. In this game every module's on-disk/in-memory import
//   directory was rewritten by the gpShell protector to a single stub
//   (`gpShell.dll!gShell`), so UnityPlayer.dll / GameAssembly.dll / the exe have
//   NO kernel32 import descriptor and doorstop's iat_hook() returns FALSE ->
//   doorstop silently gives up (its logging is compiled out).
//   The only module with a real KERNEL32 import table is gpShell.dll itself,
//   which is the resolver all module API calls funnel through.
//
// STRATEGY:  patch gpShell.dll's KERNEL32 IAT entry for GetProcAddress (and log
//   LoadLibraryW/ExW). When the game asks for "il2cpp_init", hand back our own
//   wrapper. The wrapper calls the real il2cpp_init, then performs exactly what
//   doorstop's il2cpp_doorstop_bootstrap() does: start CoreCLR from
//   <game>\dotnet\coreclr.dll and invoke BepInEx.Unity.IL2CPP's
//   Doorstop.Entrypoint.Start().
//
// config via environment:
//   KIMI_PROBE_LOG   absolute path of the log file
//   KIMI_MODE        "full" (default: install wrapper + bootstrap) | "log"
//   KIMI_FALLBACK_MS if >0 and the hook never fired, bootstrap directly after N ms
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

typedef FARPROC(WINAPI *tGPA)(HMODULE, LPCSTR);
typedef HMODULE(WINAPI *tLLW)(LPCWSTR);
typedef HMODULE(WINAPI *tLLEW)(LPCWSTR, HANDLE, DWORD);
typedef int(__cdecl *tIl2CppInit)(const char *);

static HANDLE g_log = INVALID_HANDLE_VALUE;
static tGPA  real_GPA = NULL;
static tLLW  real_LLW = NULL;
static tLLEW real_LLEW = NULL;
static tIl2CppInit real_il2cpp_init = NULL;
static volatile LONG g_gpa_calls = 0;
static volatile LONG g_il2cpp_hits = 0;
static volatile LONG g_wrapper_calls = 0;
static volatile LONG g_bootstrap_done = 0;
static volatile LONG g_bootstrap_started = 0;
static int   g_mode_full = 1;
static DWORD g_fallback_ms = 0;
static wchar_t g_gamedir[MAX_PATH];
static wchar_t g_exepath[MAX_PATH];

static void LogA(const char *fmt, ...)
{
    char buf[1536];
    int n = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "[%8lu] ", GetTickCount());
    if (n < 0) n = 0;
    va_list ap; va_start(ap, fmt);
    _vsnprintf_s(buf + n, sizeof(buf) - n, _TRUNCATE, fmt, ap);
    va_end(ap);
    size_t len = strlen(buf);
    if (len > sizeof(buf) - 3) len = sizeof(buf) - 3;
    buf[len++] = '\r'; buf[len++] = '\n'; buf[len] = 0;
    if (g_log != INVALID_HANDLE_VALUE) {
        DWORD w = 0;
        WriteFile(g_log, buf, (DWORD)len, &w, NULL);
    }
}

static void LogW(const wchar_t *fmt, ...)
{
    wchar_t wbuf[1536];
    va_list ap; va_start(ap, fmt);
    _vsnwprintf_s(wbuf, 1536, _TRUNCATE, fmt, ap);
    va_end(ap);
    int need = WideCharToMultiByte(CP_UTF8, 0, wbuf, -1, NULL, 0, NULL, NULL);
    if (need <= 0) return;
    char *nb = (char *)malloc(need + 1);
    if (!nb) return;
    WideCharToMultiByte(CP_UTF8, 0, wbuf, -1, nb, need, NULL, NULL);
    LogA("%s", nb);
    free(nb);
}

static const char *ModName(HMODULE m)
{
    static char buf[512];
    DWORD n = GetModuleFileNameA(m, buf, 400);
    if (!n) { _snprintf_s(buf, sizeof(buf), _TRUNCATE, "<h=%p>", (void *)m); return buf; }
    char *s = strrchr(buf, '\\');
    return s ? s + 1 : buf;
}

// ---------------------------------------------------------------------------
// IAT patcher
// ---------------------------------------------------------------------------
#define RVA2PTR(t, b, r) ((t)(((PCHAR)(b)) + (r)))

static int PatchIatEntry(HMODULE mod, const char *target_dll, void *from, void *to, void **saved)
{
    if (!mod) return 0;
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)mod;
    if (mz->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return 0;
    PIMAGE_IMPORT_DESCRIPTOR imp = RVA2PTR(PIMAGE_IMPORT_DESCRIPTOR, mz, rva);
    for (int i = 0; i < 128; i++) {
        if (!imp[i].Characteristics && !imp[i].FirstThunk && !imp[i].Name) break;
        char *nm = imp[i].Name ? RVA2PTR(char *, mz, imp[i].Name) : NULL;
        if (!nm || _stricmp(nm, target_dll) != 0) continue;
        if (!imp[i].FirstThunk) continue;
        void **thunk = RVA2PTR(void **, mz, imp[i].FirstThunk);
        for (int k = 0; k < 8192 && thunk[k]; k++) {
            if (thunk[k] != from) continue;
            DWORD old = 0;
            if (!VirtualProtect(&thunk[k], sizeof(void *), PAGE_READWRITE, &old)) return 0;
            if (saved) *saved = thunk[k];
            thunk[k] = to;
            VirtualProtect(&thunk[k], sizeof(void *), old, &old);
            LogA("  patch %s IAT[%d] slot %d: %p -> %p", target_dll, i, k, from, to);
            return 1;
        }
    }
    return 0;
}

// ---------------------------------------------------------------------------
// CoreCLR bootstrap (mirrors doorstop src/bootstrap.c il2cpp_doorstop_bootstrap)
// ---------------------------------------------------------------------------
typedef int (*coreclr_initialize_fn)(const char *, const char *, int,
                                     const char **, const char **, void **,
                                     unsigned int *);
typedef int (*coreclr_create_delegate_fn)(void *, unsigned int, const char *,
                                          const char *, const char *, void **);

static void PathJoin(wchar_t *out, const wchar_t *dir, const wchar_t *rel)
{
    if (rel[0] && rel[1] == L':') { lstrcpyW(out, rel); return; }
    _snwprintf_s(out, MAX_PATH, _TRUNCATE, L"%s\\%s", dir, rel);
}

static void Bootstrap(void)
{
    if (InterlockedExchange(&g_bootstrap_started, 1)) return;
    LogA("BOOTSTRAP: enter");

    wchar_t ini[MAX_PATH];
    _snwprintf_s(ini, MAX_PATH, _TRUNCATE, L"%s\\doorstop_config.ini", g_gamedir);

    wchar_t coreclr_rel[MAX_PATH] = L"", corlib_rel[MAX_PATH] = L"", target_rel[MAX_PATH] = L"";
    GetPrivateProfileStringW(L"Il2Cpp", L"coreclr_path", L"dotnet\\coreclr.dll", coreclr_rel, MAX_PATH, ini);
    GetPrivateProfileStringW(L"Il2Cpp", L"corlib_dir", L"dotnet", corlib_rel, MAX_PATH, ini);
    GetPrivateProfileStringW(L"General", L"target_assembly", L"BepInEx\\core\\BepInEx.Unity.IL2CPP.dll", target_rel, MAX_PATH, ini);

    wchar_t coreclr[MAX_PATH], corlib[MAX_PATH], target[MAX_PATH];
    PathJoin(coreclr, g_gamedir, coreclr_rel);
    PathJoin(corlib, g_gamedir, corlib_rel);
    PathJoin(target, g_gamedir, target_rel);

    LogW(L"BOOTSTRAP: coreclr=%s", coreclr);
    LogW(L"BOOTSTRAP: corlib=%s", corlib);
    LogW(L"BOOTSTRAP: target=%s", target);

    if (GetFileAttributesW(coreclr) == INVALID_FILE_ATTRIBUTES) { LogA("BOOTSTRAP FAIL: coreclr missing"); return; }
    if (GetFileAttributesW(target) == INVALID_FILE_ATTRIBUTES) { LogA("BOOTSTRAP FAIL: target assembly missing"); return; }

    // target dir + assembly simple name
    wchar_t target_dir[MAX_PATH];
    lstrcpyW(target_dir, target);
    wchar_t *slash = wcsrchr(target_dir, L'\\');
    wchar_t target_name[260] = L"";
    if (slash) { lstrcpyW(target_name, slash + 1); *slash = 0; }
    wchar_t *dot = wcsrchr(target_name, L'.');
    if (dot) *dot = 0;

    wchar_t app_paths[MAX_PATH * 2];
    _snwprintf_s(app_paths, MAX_PATH * 2, _TRUNCATE, L"%s;%s", corlib, target_dir);

    SetEnvironmentVariableW(L"DOORSTOP_INITIALIZED", L"TRUE");
    SetEnvironmentVariableW(L"DOORSTOP_INVOKE_DLL_PATH", target);
    SetEnvironmentVariableW(L"DOORSTOP_MANAGED_FOLDER_DIR", corlib);
    SetEnvironmentVariableW(L"DOORSTOP_PROCESS_PATH", g_exepath);
    SetEnvironmentVariableW(L"DOORSTOP_DLL_SEARCH_DIRS", app_paths);
    SetEnvironmentVariableW(L"DOORSTOP_DISABLE", L"TRUE");

    HMODULE cm = LoadLibraryW(coreclr);
    LogA("BOOTSTRAP: LoadLibrary(coreclr) = %p err=%lu", (void *)cm, cm ? 0 : GetLastError());
    if (!cm) return;
    coreclr_initialize_fn cinit = (coreclr_initialize_fn)GetProcAddress(cm, "coreclr_initialize");
    coreclr_create_delegate_fn cdeleg = (coreclr_create_delegate_fn)GetProcAddress(cm, "coreclr_create_delegate");
    LogA("BOOTSTRAP: coreclr_initialize=%p coreclr_create_delegate=%p", (void *)cinit, (void *)cdeleg);
    if (!cinit || !cdeleg) return;

    char *exe_n = NULL, *paths_n = NULL;
    {
        int n1 = WideCharToMultiByte(CP_UTF8, 0, g_exepath, -1, NULL, 0, NULL, NULL);
        exe_n = (char *)malloc(n1); WideCharToMultiByte(CP_UTF8, 0, g_exepath, -1, exe_n, n1, NULL, NULL);
        int n2 = WideCharToMultiByte(CP_UTF8, 0, app_paths, -1, NULL, 0, NULL, NULL);
        paths_n = (char *)malloc(n2); WideCharToMultiByte(CP_UTF8, 0, app_paths, -1, paths_n, n2, NULL, NULL);
    }
    LogA("BOOTSTRAP: APP_PATHS=%s", paths_n);
    LogA("BOOTSTRAP: exePath=%s", exe_n);

    const char *props[1] = { "APP_PATHS" };
    const char *vals[1]  = { paths_n };
    void *host = NULL;
    unsigned int domain_id = 0;
    int rc = cinit(exe_n, "Doorstop Domain", 1, props, vals, &host, &domain_id);
    LogA("BOOTSTRAP: coreclr_initialize rc=0x%08x host=%p domain=%u", rc, host, domain_id);
    if (rc != 0) return;

    char target_name_n[260];
    WideCharToMultiByte(CP_UTF8, 0, target_name, -1, target_name_n, 260, NULL, NULL);

    void (*startup)() = NULL;
    rc = cdeleg(host, domain_id, target_name_n, "Doorstop.Entrypoint", "Start", (void **)&startup);
    LogA("BOOTSTRAP: create_delegate(%s, Doorstop.Entrypoint::Start) rc=0x%08x fn=%p", target_name_n, rc, (void *)startup);
    if (rc != 0 || !startup) return;

    LogA("BOOTSTRAP: invoking Doorstop.Entrypoint.Start()");
    __try {
        startup();
        LogA("BOOTSTRAP: Doorstop.Entrypoint.Start() returned normally");
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogA("BOOTSTRAP: exception 0x%08lx inside Doorstop.Entrypoint.Start()", GetExceptionCode());
    }
    InterlockedExchange(&g_bootstrap_done, 1);
    LogA("BOOTSTRAP: done");
}

static int __cdecl our_il2cpp_init(const char *domain_name)
{
    InterlockedIncrement(&g_wrapper_calls);
    LogA("WRAPPER: il2cpp_init(\"%s\") entered", domain_name ? domain_name : "(null)");
    int r = 0;
    if (real_il2cpp_init) {
        __try { r = real_il2cpp_init(domain_name); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LogA("WRAPPER: exception 0x%08lx in original il2cpp_init", GetExceptionCode());
        }
        LogA("WRAPPER: original il2cpp_init returned %d", r);
    } else {
        LogA("WRAPPER: real_il2cpp_init is NULL!");
    }
    Bootstrap();
    LogA("WRAPPER: returning %d", r);
    return r;
}

// ---------------------------------------------------------------------------
// detours
// ---------------------------------------------------------------------------
static FARPROC WINAPI det_GPA(HMODULE m, LPCSTR name)
{
    InterlockedIncrement(&g_gpa_calls);
    if (name) {
        if (strcmp(name, "il2cpp_init") == 0) {
            InterlockedIncrement(&g_il2cpp_hits);
            LogA("GPA(%s, il2cpp_init) intercepted -> wrapper", ModName(m));
            if (g_mode_full) {
                if (!real_il2cpp_init)
                    real_il2cpp_init = (tIl2CppInit)real_GPA(m, name);
                return (FARPROC)&our_il2cpp_init;
            }
        } else {
            LogA("GPA(%s, %s)", ModName(m), name);
        }
    }
    return real_GPA(m, name);
}

static HMODULE WINAPI det_LLW(LPCWSTR name)
{
    LogW(L"LoadLibraryW(%s)", name ? name : L"(null)");
    return real_LLW(name);
}

static HMODULE WINAPI det_LLEW(LPCWSTR name, HANDLE f, DWORD flags)
{
    LogW(L"LoadLibraryExW(%s, flags=0x%lx)", name ? name : L"(null)", flags);
    return real_LLEW(name, f, flags);
}

// ---------------------------------------------------------------------------
static DWORD WINAPI Watchdog(LPVOID p)
{
    (void)p;
    DWORD start = GetTickCount();
    for (;;) {
        Sleep(1000);
        DWORD el = GetTickCount() - start;
        LogA("WATCH: t=%lums gpa_calls=%ld il2cpp_hits=%ld wrapper=%ld bootstrap_started=%ld done=%ld",
             el, g_gpa_calls, g_il2cpp_hits, g_wrapper_calls, g_bootstrap_started, g_bootstrap_done);
        if (g_fallback_ms && el > g_fallback_ms && !g_wrapper_calls) {
            LogA("FALLBACK: hook never fired after %lums, bootstrapping directly", el);
            g_fallback_ms = 0;
            Bootstrap();
        }
    }
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    wchar_t logpath[MAX_PATH] = L"";
    DWORD n = GetEnvironmentVariableW(L"KIMI_PROBE_LOG", logpath, MAX_PATH);
    if (!(n > 0 && n < MAX_PATH)) {
        wchar_t self[MAX_PATH];
        DWORD m = GetModuleFileNameW(hModule, self, MAX_PATH);
        if (m > 0 && m < MAX_PATH) {
            wchar_t *s = wcsrchr(self, L'\\');
            if (s) { *s = 0; _snwprintf_s(logpath, MAX_PATH, _TRUNCATE, L"%s\\probe3.txt", self); }
        }
    }
    g_log = CreateFileW(logpath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_log == INVALID_HANDLE_VALUE) return TRUE;

    wchar_t mode[32] = L"full";
    GetEnvironmentVariableW(L"KIMI_MODE", mode, 32);
    g_mode_full = (_wcsicmp(mode, L"log") != 0);

    wchar_t fb[32] = L"0";
    GetEnvironmentVariableW(L"KIMI_FALLBACK_MS", fb, 32);
    g_fallback_ms = (DWORD)_wtoi(fb);

    GetModuleFileNameW(NULL, g_exepath, MAX_PATH);
    lstrcpyW(g_gamedir, g_exepath);
    { wchar_t *s = wcsrchr(g_gamedir, L'\\'); if (s) *s = 0; }

    LogA("");
    LogA("===== PROBE3 attach pid=%lu tid=%lu mode=%s fallback=%lums =====",
         GetCurrentProcessId(), GetCurrentThreadId(), g_mode_full ? "full" : "log", g_fallback_ms);
    LogW(L"PROBE3: exe=%s", g_exepath);
    LogW(L"PROBE3: dir=%s", g_gamedir);

    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    real_GPA = (tGPA)GetProcAddress(k32, "GetProcAddress");
    real_LLW = (tLLW)GetProcAddress(k32, "LoadLibraryW");
    real_LLEW = (tLLEW)GetProcAddress(k32, "LoadLibraryExW");
    LogA("real GPA=%p LLW=%p LLEW=%p", (void *)real_GPA, (void *)real_LLW, (void *)real_LLEW);

    HMODULE shell = GetModuleHandleW(L"gpShell.dll");
    LogA("gpShell=%p", (void *)shell);
    int p1 = 0, p2 = 0, p3 = 0;
    if (shell) {
        p1 = PatchIatEntry(shell, "KERNEL32.dll", (void *)real_GPA, (void *)det_GPA, NULL);
        p2 = PatchIatEntry(shell, "KERNEL32.dll", (void *)real_LLW, (void *)det_LLW, NULL);
        p3 = PatchIatEntry(shell, "KERNEL32.dll", (void *)real_LLEW, (void *)det_LLEW, NULL);
    }
    LogA("PATCH results: GetProcAddress=%d LoadLibraryW=%d LoadLibraryExW=%d", p1, p2, p3);

    HMODULE up = GetModuleHandleW(L"UnityPlayer.dll");
    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    LogA("UnityPlayer=%p GameAssembly=%p", (void *)up, (void *)ga);
    if (ga) {
        tIl2CppInit ii = (tIl2CppInit)GetProcAddress(ga, "il2cpp_init");
        LogA("direct GetProcAddress(GameAssembly, il2cpp_init) = %p", (void *)ii);
        if (!real_il2cpp_init) real_il2cpp_init = ii;
    } else {
        LogA("GameAssembly not loaded yet at attach");
    }

    CreateThread(NULL, 0, Watchdog, NULL, 0, NULL);
    return TRUE;
}
