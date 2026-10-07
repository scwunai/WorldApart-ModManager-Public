// probe5.c - hook il2cpp_init by rewriting its first bytes, then run doorstop's
//            CoreCLR bootstrap so BepInEx.Unity.IL2CPP starts.
//
// Measured facts that shaped this (see KIMI\实施报告.md):
//   * all modules' import tables were stripped by gpShell to a single
//     `gpShell.dll!gShell` stub, so doorstop 4.3's iat_hook() on UnityPlayer.dll
//     can never succeed -> doorstop silently does nothing
//   * patching gpShell.dll's own KERNEL32 IAT makes Unity abort with
//     "Failed to load il2cpp" (see UnityPlayer.dll string) -> gpShell must not be
//     touched at all
//   * rewriting GameAssembly's EXPORT table entry for il2cpp_init (probe4) is
//     accepted by the game but nothing ever resolves il2cpp_init by name -> the
//     EAT route cannot work
//   * GameAssembly.dll is NOT loaded when we inject (~46 ms into the child), so
//     redirecting the function body itself is possible
//
// STRATEGY: wait for GameAssembly.dll (DLL load notification + 1 ms polling),
//   then overwrite the first 12 bytes of il2cpp_init with `mov rax,hook; jmp rax`.
//   The wrapper restores the original bytes, calls the real il2cpp_init, re-arms
//   the hook and then runs doorstop's il2cpp bootstrap (start CoreCLR from
//   <game>\dotnet\coreclr.dll, invoke BepInEx.Unity.IL2CPP's
//   Doorstop.Entrypoint.Start()).
//
// env: KIMI_PROBE_LOG, KIMI_MODE=full|log, KIMI_FALLBACK_MS, KIMI_USE_NOTIFY
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

typedef int(__cdecl *tIl2CppInit)(const char *);

static HANDLE g_log = INVALID_HANDLE_VALUE;
static tIl2CppInit real_il2cpp_init = NULL;
static volatile LONG g_wrapper_calls = 0;
static volatile LONG g_bootstrap_done = 0;
static volatile LONG g_bootstrap_started = 0;
static volatile LONG g_hooked = 0;
static int   g_mode_full = 1;
static DWORD g_fallback_ms = 0;
static int   g_use_notify = 1;
static wchar_t g_gamedir[MAX_PATH];
static wchar_t g_exepath[MAX_PATH];

static unsigned char g_saved[16];
static unsigned char g_patch[16];
static void *g_target = NULL;
static CRITICAL_SECTION g_cs;

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
    if (g_log != INVALID_HANDLE_VALUE) { DWORD w = 0; WriteFile(g_log, buf, (DWORD)len, &w, NULL); }
}

#define RVA2PTR(t, b, r) ((t)(((PCHAR)(b)) + (r)))

// ---------------------------------------------------------------------------
// CoreCLR bootstrap (mirrors doorstop src/bootstrap.c il2cpp_doorstop_bootstrap)
// ---------------------------------------------------------------------------
typedef int (*coreclr_initialize_fn)(const char *, const char *, int,
                                     const char **, const char **, void **, unsigned int *);
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
    LogA("BOOTSTRAP: coreclr=%ls", coreclr);
    LogA("BOOTSTRAP: target=%ls", target);
    if (GetFileAttributesW(coreclr) == INVALID_FILE_ATTRIBUTES) { LogA("BOOTSTRAP FAIL: coreclr missing"); return; }
    if (GetFileAttributesW(target) == INVALID_FILE_ATTRIBUTES) { LogA("BOOTSTRAP FAIL: target missing"); return; }

    wchar_t target_dir[MAX_PATH], target_name[260] = L"";
    lstrcpyW(target_dir, target);
    wchar_t *slash = wcsrchr(target_dir, L'\\');
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
    LogA("BOOTSTRAP: LoadLibrary(coreclr)=%p err=%lu", (void *)cm, cm ? 0 : GetLastError());
    if (!cm) return;
    coreclr_initialize_fn cinit = (coreclr_initialize_fn)GetProcAddress(cm, "coreclr_initialize");
    coreclr_create_delegate_fn cdeleg = (coreclr_create_delegate_fn)GetProcAddress(cm, "coreclr_create_delegate");
    LogA("BOOTSTRAP: cinit=%p cdeleg=%p", (void *)cinit, (void *)cdeleg);
    if (!cinit || !cdeleg) return;

    int n1 = WideCharToMultiByte(CP_UTF8, 0, g_exepath, -1, NULL, 0, NULL, NULL);
    char *exe_n = (char *)malloc(n1); WideCharToMultiByte(CP_UTF8, 0, g_exepath, -1, exe_n, n1, NULL, NULL);
    int n2 = WideCharToMultiByte(CP_UTF8, 0, app_paths, -1, NULL, 0, NULL, NULL);
    char *paths_n = (char *)malloc(n2); WideCharToMultiByte(CP_UTF8, 0, app_paths, -1, paths_n, n2, NULL, NULL);
    LogA("BOOTSTRAP: APP_PATHS=%s", paths_n);

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
        LogA("BOOTSTRAP: exception 0x%08lx inside Start()", GetExceptionCode());
    }
    InterlockedExchange(&g_bootstrap_done, 1);
    LogA("BOOTSTRAP: done");
}

// ---------------------------------------------------------------------------
// inline hook of il2cpp_init
// ---------------------------------------------------------------------------
static void WriteBytes(void *dst, const unsigned char *src, SIZE_T n)
{
    DWORD old = 0;
    if (!VirtualProtect(dst, n, PAGE_EXECUTE_READWRITE, &old)) {
        LogA("HOOK: VirtualProtect(%p) err=%lu", dst, GetLastError());
        return;
    }
    memcpy(dst, src, n);
    VirtualProtect(dst, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), dst, n);
}

static int __cdecl our_il2cpp_init(const char *domain_name)
{
    InterlockedIncrement(&g_wrapper_calls);
    LogA("WRAPPER: il2cpp_init(\"%s\") entered", domain_name ? domain_name : "(null)");

    EnterCriticalSection(&g_cs);
    WriteBytes(g_target, g_saved, 16);        /* restore original prologue */
    LeaveCriticalSection(&g_cs);

    int r = 0;
    __try { r = real_il2cpp_init(domain_name); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogA("WRAPPER: exception 0x%08lx in original il2cpp_init", GetExceptionCode());
    }
    LogA("WRAPPER: original il2cpp_init returned %d", r);

    EnterCriticalSection(&g_cs);
    WriteBytes(g_target, g_patch, 16);        /* re-arm (idempotent) */
    LeaveCriticalSection(&g_cs);
    LogA("WRAPPER: hook re-armed");

    if (g_mode_full) Bootstrap();
    else LogA("WRAPPER: mode=log, bootstrap skipped");

    LogA("WRAPPER: returning %d", r);
    return r;
}

static int HookIl2cppInit(void *ga)
{
    if (g_hooked) return 1;
    void *fn = (void *)GetProcAddress((HMODULE)ga, "il2cpp_init");
    LogA("HOOK: GameAssembly=%p il2cpp_init=%p", ga, fn);
    if (!fn) { LogA("HOOK FAIL: il2cpp_init not exported"); return 0; }
    g_target = fn;
    memcpy(g_saved, fn, 16);
    LogA("HOOK: prologue %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x",
         g_saved[0], g_saved[1], g_saved[2], g_saved[3], g_saved[4], g_saved[5], g_saved[6], g_saved[7],
         g_saved[8], g_saved[9], g_saved[10], g_saved[11], g_saved[12], g_saved[13], g_saved[14], g_saved[15]);
    g_patch[0] = 0x48; g_patch[1] = 0xB8;                    /* mov rax, imm64 */
    *(void **)(g_patch + 2) = (void *)&our_il2cpp_init;
    g_patch[10] = 0xFF; g_patch[11] = 0xE0;                  /* jmp rax */
    g_patch[12] = 0xCC; g_patch[13] = 0xCC; g_patch[14] = 0xCC; g_patch[15] = 0xCC;

    real_il2cpp_init = (tIl2CppInit)fn;
    EnterCriticalSection(&g_cs);
    WriteBytes(fn, g_patch, 16);
    LeaveCriticalSection(&g_cs);
    InterlockedExchange(&g_hooked, 1);
    LogA("HOOK: il2cpp_init patched (%d bytes stolen)", 16);
    return 1;
}

// ---------------------------------------------------------------------------
// DLL load notification
// ---------------------------------------------------------------------------
typedef struct { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } MY_UNICODE_STRING;
typedef struct {
    ULONG Flags;
    MY_UNICODE_STRING *FullDllName;
    MY_UNICODE_STRING *BaseDllName;
    PVOID DllBase;
    ULONG SizeOfImage;
} LDR_DLL_LOADED_NOTIFICATION_DATA;
typedef struct { ULONG Flags; LDR_DLL_LOADED_NOTIFICATION_DATA Loaded; } LDR_DLL_NOTIFICATION_DATA;
typedef VOID(NTAPI *PLDR_DLL_NOTIFICATION_FUNCTION)(ULONG, const LDR_DLL_NOTIFICATION_DATA *, PVOID);
typedef NTSTATUS(NTAPI *pLdrRegisterDllNotification)(ULONG, PLDR_DLL_NOTIFICATION_FUNCTION, PVOID, PVOID *);
#define LDR_DLL_NOTIFICATION_REASON_LOADED 1

static volatile LONG g_notify_count = 0;

static VOID NTAPI DllNotify(ULONG reason, const LDR_DLL_NOTIFICATION_DATA *data, PVOID ctx)
{
    (void)ctx;
    if (reason != LDR_DLL_NOTIFICATION_REASON_LOADED) return;
    LONG c = InterlockedIncrement(&g_notify_count);
    if (!data || !data->Loaded.BaseDllName || !data->Loaded.BaseDllName->Buffer) {
        if (c <= 8) LogA("DLLNOTIFY #%ld: null data", c);
        return;
    }
    if (c <= 8) LogA("DLLNOTIFY #%ld: %ls base=%p", c, data->Loaded.BaseDllName->Buffer, data->Loaded.DllBase);
    if (_wcsicmp(data->Loaded.BaseDllName->Buffer, L"GameAssembly.dll") != 0) return;
    if (g_hooked) return;
    LogA("DLLNOTIFY: GameAssembly base=%p", data->Loaded.DllBase);
    HookIl2cppInit(data->Loaded.DllBase);
}

// ---------------------------------------------------------------------------
static DWORD WINAPI Watchdog(LPVOID p)
{
    (void)p;
    typedef UINT(WINAPI *tTBP)(UINT);
    HMODULE wm = LoadLibraryW(L"winmm.dll");
    if (wm) { tTBP tbp = (tTBP)GetProcAddress(wm, "timeBeginPeriod"); if (tbp) tbp(1); }

    DWORD start = GetTickCount();
    while (!g_hooked && GetTickCount() - start < 60000) {
        HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
        if (ga) {
            LogA("POLL: GameAssembly at %p (t=%lums)", (void *)ga, GetTickCount() - start);
            HookIl2cppInit(ga);
            break;
        }
        Sleep(1);
    }

    for (;;) {
        Sleep(1000);
        DWORD el = GetTickCount() - start;
        LogA("WATCH: t=%lums hooked=%ld wrapper=%ld boot=%ld done=%ld",
             el, g_hooked, g_wrapper_calls, g_bootstrap_started, g_bootstrap_done);
        if (g_fallback_ms && el > g_fallback_ms && !g_wrapper_calls) {
            LogA("FALLBACK: hook never fired after %lums, bootstrapping directly", el);
            g_fallback_ms = 0;
            if (g_mode_full) Bootstrap();
        }
    }
    return 0;
}

static DWORD WINAPI InitThread(LPVOID param)
{
    HMODULE hModule = (HMODULE)param;
    wchar_t logpath[MAX_PATH] = L"";
    DWORD n = GetEnvironmentVariableW(L"KIMI_PROBE_LOG", logpath, MAX_PATH);
    if (!(n > 0 && n < MAX_PATH)) {
        wchar_t self[MAX_PATH];
        DWORD m = GetModuleFileNameW(hModule, self, MAX_PATH);
        if (m > 0 && m < MAX_PATH) {
            wchar_t *s = wcsrchr(self, L'\\');
            if (s) { *s = 0; _snwprintf_s(logpath, MAX_PATH, _TRUNCATE, L"%s\\probe5.txt", self); }
        }
    }
    g_log = CreateFileW(logpath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_log == INVALID_HANDLE_VALUE) return 0;

    wchar_t mode[32] = L"full";
    GetEnvironmentVariableW(L"KIMI_MODE", mode, 32);
    g_mode_full = (_wcsicmp(mode, L"log") != 0);
    wchar_t fb[32] = L"0";
    GetEnvironmentVariableW(L"KIMI_FALLBACK_MS", fb, 32);
    g_fallback_ms = (DWORD)_wtoi(fb);
    wchar_t o[32] = L"1";
    GetEnvironmentVariableW(L"KIMI_USE_NOTIFY", o, 32);
    g_use_notify = (_wtoi(o) != 0);

    GetModuleFileNameW(NULL, g_exepath, MAX_PATH);
    lstrcpyW(g_gamedir, g_exepath);
    { wchar_t *s = wcsrchr(g_gamedir, L'\\'); if (s) *s = 0; }

    InitializeCriticalSection(&g_cs);

    LogA("");
    LogA("===== PROBE5 attach pid=%lu tid=%lu mode=%s fallback=%lums notify=%d =====",
         GetCurrentProcessId(), GetCurrentThreadId(), g_mode_full ? "full" : "log", g_fallback_ms, g_use_notify);

    if (g_use_notify) {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        pLdrRegisterDllNotification reg =
            ntdll ? (pLdrRegisterDllNotification)GetProcAddress(ntdll, "LdrRegisterDllNotification") : NULL;
        PVOID cookie = NULL;
        NTSTATUS st = 0;
        if (reg) st = reg(0, DllNotify, NULL, &cookie);
        LogA("LdrRegisterDllNotification=%p status=0x%08lx cookie=%p", (void *)reg, (unsigned long)st, cookie);
    }

    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    if (ga) { LogA("GameAssembly already loaded: %p", (void *)ga); HookIl2cppInit(ga); }
    else   { LogA("GameAssembly not loaded yet (good)"); }

    CreateThread(NULL, 0, Watchdog, NULL, 0, NULL);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(hModule);
    HANDLE h = CreateThread(NULL, 0, InitThread, (LPVOID)hModule, 0, NULL);
    if (h) CloseHandle(h);
    return TRUE;
}
