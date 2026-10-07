// probe4.c - redirect GameAssembly!il2cpp_init via its EXPORT ADDRESS TABLE
//
// Measured facts driving this (see KIMI\实施报告.md):
//   * module imports were stripped by gpShell to a single `gpShell.dll!gShell`
//     stub, so doorstop's iat_hook() on UnityPlayer can never succeed
//   * patching gpShell's KERNEL32!GetProcAddress IAT shows only ~169 early
//     resolutions and NEVER sees "il2cpp_init" => the protector does not resolve
//     il2cpp_init through that path
//   * GameAssembly.dll is NOT loaded yet when we inject (~90 ms into the child)
//     => we are early enough to redirect the export before anyone reads it
//
// STRATEGY: as soon as GameAssembly.dll is mapped (DLL load notification, plus a
//   1 ms polling fallback), rewrite its EAT entry for `il2cpp_init` to point at a
//   12-byte absolute jump stub placed within +-2 GB of the module. Any name-based
//   resolver (GetProcAddress, LdrGetProcedureAddress or the protector's own EAT
//   walker) then gets our wrapper. The wrapper calls the original il2cpp_init and
//   then performs doorstop's il2cpp bootstrap: start CoreCLR from
//   <game>\dotnet\coreclr.dll and invoke BepInEx.Unity.IL2CPP's
//   Doorstop.Entrypoint.Start().
//
// env:
//   KIMI_PROBE_LOG    log file (absolute path)
//   KIMI_MODE         "full" (default) | "log" (patch EAT but no bootstrap)
//   KIMI_FALLBACK_MS  if >0 and the hook never fires, bootstrap directly after N ms
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>

typedef FARPROC(WINAPI *tGPA)(HMODULE, LPCSTR);
typedef int(__cdecl *tIl2CppInit)(const char *);

static HANDLE g_log = INVALID_HANDLE_VALUE;
static tGPA  real_GPA = NULL;
static tIl2CppInit real_il2cpp_init = NULL;
static volatile LONG g_gpa_calls = 0;
static volatile LONG g_il2cpp_hits = 0;
static volatile LONG g_wrapper_calls = 0;
static volatile LONG g_bootstrap_done = 0;
static volatile LONG g_bootstrap_started = 0;
static volatile LONG g_eat_patched = 0;
static int   g_mode_full = 1;
static int   g_patch_gpshell = 0;
static int   g_use_notify = 0;
static DWORD g_fallback_ms = 0;
static wchar_t g_gamedir[MAX_PATH];
static wchar_t g_exepath[MAX_PATH];
static void *g_stub = NULL;

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

static const char *ModName(HMODULE m)
{
    static char buf[512];
    DWORD n = GetModuleFileNameA(m, buf, 400);
    if (!n) { _snprintf_s(buf, sizeof(buf), _TRUNCATE, "<h=%p>", (void *)m); return buf; }
    char *s = strrchr(buf, '\\');
    return s ? s + 1 : buf;
}

#define RVA2PTR(t, b, r) ((t)(((PCHAR)(b)) + (r)))

// ---------------------------------------------------------------------------
// near allocation for the jump stub
// ---------------------------------------------------------------------------
typedef struct {
    PVOID LowestStartingAddress;
    PVOID HighestEndingAddress;
    SIZE_T Alignment;
} MY_MEM_ADDRESS_REQUIREMENTS;

typedef struct {
    ULONGLONG Type : 8;
    ULONGLONG Reserved : 56;
    union { ULONGLONG ULong64; PVOID Pointer; SIZE_T Size; HANDLE Handle; ULONG ULong; } u;
} MY_MEM_EXTENDED_PARAMETER;

typedef PVOID(WINAPI *pVirtualAlloc2)(HANDLE, PVOID, SIZE_T, ULONG, ULONG,
                                      MY_MEM_EXTENDED_PARAMETER *, ULONG);

static void *AllocNear(void *base)
{
    HMODULE kb = GetModuleHandleW(L"kernelbase.dll");
    pVirtualAlloc2 va2 = kb ? (pVirtualAlloc2)GetProcAddress(kb, "VirtualAlloc2") : NULL;
    ULONG_PTR b = (ULONG_PTR)base;
    if (va2) {
        MY_MEM_ADDRESS_REQUIREMENTS req;
        memset(&req, 0, sizeof(req));
        /* the EAT stores a 32-bit unsigned RVA, so the stub must live ABOVE the
           module base and within 2 GB of it */
        req.LowestStartingAddress = (PVOID)(b + 0x10000);
        req.HighestEndingAddress = (PVOID)(b + 0x70000000ULL);
        req.Alignment = 0x1000;
        MY_MEM_EXTENDED_PARAMETER p;
        memset(&p, 0, sizeof(p));
        p.Type = 1;                 /* MemExtendedParameterAddressRequirements */
        p.u.Pointer = &req;
        void *m = va2(NULL, NULL, 0x1000, MEM_COMMIT | MEM_RESERVE,
                      PAGE_EXECUTE_READWRITE, &p, 1);
        if (m) return m;
        LogA("AllocNear: VirtualAlloc2(req) failed, err=%lu", GetLastError());
    }
    // hinted fallback: step upward from the module base until a free page is found
    for (int i = 1; i <= 1024; i++) {
        ULONG_PTR hint = b + (ULONG_PTR)i * 0x10000;
        void *m = VirtualAlloc((LPVOID)hint, 0x1000, MEM_COMMIT | MEM_RESERVE,
                               PAGE_EXECUTE_READWRITE);
        if (!m) continue;
        long long d = (long long)((ULONG_PTR)m - b);
        if (d > 0 && d < 0x70000000LL) return m;
        VirtualFree(m, 0, MEM_RELEASE);
    }
    return NULL;
}

// ---------------------------------------------------------------------------
// EAT patch: name -> new function pointer
// ---------------------------------------------------------------------------
static void *FindExportByName(void *mod, const char *name, DWORD *out_ordinal)
{
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)mod;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    DWORD er = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    if (!er) return NULL;
    PIMAGE_EXPORT_DIRECTORY exp = RVA2PTR(PIMAGE_EXPORT_DIRECTORY, mz, er);
    DWORD *names = RVA2PTR(DWORD *, mz, exp->AddressOfNames);
    WORD  *ords  = RVA2PTR(WORD *, mz, exp->AddressOfNameOrdinals);
    DWORD *funcs = RVA2PTR(DWORD *, mz, exp->AddressOfFunctions);
    for (DWORD i = 0; i < exp->NumberOfNames; i++) {
        char *n = RVA2PTR(char *, mz, names[i]);
        if (strcmp(n, name) == 0) {
            WORD o = ords[i];
            if (out_ordinal) *out_ordinal = o;
            return (void *)funcs; /* caller uses ordinal; return table base */
        }
    }
    return NULL;
}

// returns 1 on success; *old_rva gets the pre-patch RVA
static int PatchEat(void *mod, const char *name, void *stub, DWORD *old_rva)
{
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)mod;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    DWORD er = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    DWORD esz = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].Size;
    if (!er) return 0;
    PIMAGE_EXPORT_DIRECTORY exp = RVA2PTR(PIMAGE_EXPORT_DIRECTORY, mz, er);
    DWORD *names = RVA2PTR(DWORD *, mz, exp->AddressOfNames);
    WORD  *ords  = RVA2PTR(WORD *, mz, exp->AddressOfNameOrdinals);
    DWORD *funcs = RVA2PTR(DWORD *, mz, exp->AddressOfFunctions);
    for (DWORD i = 0; i < exp->NumberOfNames; i++) {
        char *n = RVA2PTR(char *, mz, names[i]);
        if (strcmp(n, name) != 0) continue;
        WORD o = ords[i];
        if (o >= exp->NumberOfFunctions) return 0;
        if (old_rva) *old_rva = funcs[o];
        ULONG_PTR stub_rva = (ULONG_PTR)stub - (ULONG_PTR)mod;
        if (stub_rva > 0x7FFFFFFFULL) { LogA("EAT FAIL: stub too far from module"); return 0; }
        DWORD oldp = 0;
        if (!VirtualProtect(&funcs[o], sizeof(DWORD), PAGE_READWRITE, &oldp)) return 0;
        funcs[o] = (DWORD)stub_rva;
        VirtualProtect(&funcs[o], sizeof(DWORD), oldp, &oldp);
        LogA("EAT: %s EAT[ord %u] 0x%lx -> rva 0x%lx (stub %p)", name, o, *old_rva, (DWORD)stub_rva, stub);
        return 1;
    }
    LogA("EAT FAIL: export %s not found", name);
    return 0;
}

static int __cdecl our_il2cpp_init(const char *domain_name);

// Find a run of padding bytes (0xCC/0x00) inside an executable section of the
// module. Such padding sits between functions and is never executed, and - unlike
// a fresh VirtualAlloc result - it is guaranteed to be inside the module, so its
// RVA is representable in the export address table. (Measured fact: the 2 GB
// above GameAssembly is already fully committed, so a separate stub allocation
// cannot be used as an EAT target.)
static void *FindCodeCave(void *mod, SIZE_T need)
{
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)mod;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    PIMAGE_SECTION_HEADER sec = IMAGE_FIRST_SECTION(nt);
    for (int pass = 0; pass < 2; pass++) {
        unsigned char fill = pass == 0 ? 0xCC : 0x00;
        for (int i = 0; i < nt->FileHeader.NumberOfSections; i++) {
            if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
            SIZE_T sz = sec[i].Misc.VirtualSize;
            if (sec[i].SizeOfRawData < sz) sz = sec[i].SizeOfRawData;
            if (sz < need) continue;
            unsigned char *p = (unsigned char *)mod + sec[i].VirtualAddress;
            SIZE_T run = 0;
            for (SIZE_T k = 0; k < sz; k++) {
                if (p[k] == fill) {
                    run++;
                    if (run >= need) {
                        void *cave = p + k + 1 - need;
                        LogA("CAVE: found %lu x0x%02X at %p (rva 0x%lx, section %.8s)",
                             (unsigned long)need, fill, cave,
                             (unsigned long)((ULONG_PTR)cave - (ULONG_PTR)mod), sec[i].Name);
                        return cave;
                    }
                } else {
                    run = 0;
                }
            }
        }
    }
    return NULL;
}

static int PatchGameAssembly(void *ga)
{
    if (g_eat_patched) return 0;
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)ga;
    if (mz->e_magic != IMAGE_DOS_SIGNATURE) { LogA("EAT: bad DOS magic at %p", ga); return 0; }
    if (!g_stub) {
        g_stub = FindCodeCave(ga, 32);
        if (!g_stub) g_stub = AllocNear(ga);
    }
    LogA("EAT: GameAssembly=%p stub=%p (rva=%lld)", ga, g_stub,
         g_stub ? (long long)((ULONG_PTR)g_stub - (ULONG_PTR)ga) : -1LL);
    if (!g_stub) { LogA("EAT FAIL: no usable stub location"); return 0; }

    unsigned char code[16];
    code[0] = 0x48; code[1] = 0xB8;                       /* mov rax, imm64 */
    *(void **)(code + 2) = (void *)&our_il2cpp_init;
    code[10] = 0xFF; code[11] = 0xE0;                     /* jmp rax */
    DWORD oldp = 0;
    if (!VirtualProtect(g_stub, 32, PAGE_EXECUTE_READWRITE, &oldp)) {
        LogA("EAT FAIL: VirtualProtect(stub) err=%lu", GetLastError());
        g_stub = NULL;
        return 0;
    }
    memcpy(g_stub, code, 12);
    memset((unsigned char *)g_stub + 12, 0xCC, 4);
    VirtualProtect(g_stub, 32, oldp, &oldp);
    FlushInstructionCache(GetCurrentProcess(), g_stub, 16);

    DWORD old_rva = 0;
    if (!PatchEat(ga, "il2cpp_init", g_stub, &old_rva)) return 0;
    real_il2cpp_init = (tIl2CppInit)((PCHAR)ga + old_rva);
    LogA("EAT: real il2cpp_init = %p", (void *)real_il2cpp_init);
    InterlockedExchange(&g_eat_patched, 1);
    return 1;
}

// ---------------------------------------------------------------------------
// (optional) extra net: gpShell KERNEL32 IAT GetProcAddress detour, log only
// ---------------------------------------------------------------------------
static FARPROC WINAPI det_GPA(HMODULE m, LPCSTR name)
{
    InterlockedIncrement(&g_gpa_calls);
    if (name) {
        if (strcmp(name, "il2cpp_init") == 0) {
            InterlockedIncrement(&g_il2cpp_hits);
            LogA("GPA(%s, il2cpp_init) intercepted", ModName(m));
            if (g_mode_full && real_il2cpp_init) return (FARPROC)&our_il2cpp_init;
        }
    }
    return real_GPA(m, name);
}

static int PatchIatGpa(HMODULE shell, void *from, void *to)
{
    PIMAGE_DOS_HEADER mz = (PIMAGE_DOS_HEADER)shell;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return 0;
    PIMAGE_IMPORT_DESCRIPTOR imp = RVA2PTR(PIMAGE_IMPORT_DESCRIPTOR, mz, rva);
    for (int i = 0; i < 128 && (imp[i].Characteristics || imp[i].FirstThunk || imp[i].Name); i++) {
        char *nm = imp[i].Name ? RVA2PTR(char *, mz, imp[i].Name) : NULL;
        if (!nm || _stricmp(nm, "KERNEL32.dll") != 0) continue;
        void **thunk = RVA2PTR(void **, mz, imp[i].FirstThunk);
        for (int k = 0; k < 8192 && thunk[k]; k++) {
            if (thunk[k] != from) continue;
            DWORD old = 0;
            if (!VirtualProtect(&thunk[k], sizeof(void *), PAGE_READWRITE, &old)) return 0;
            thunk[k] = to;
            VirtualProtect(&thunk[k], sizeof(void *), old, &old);
            return 1;
        }
    }
    return 0;
}

// ---------------------------------------------------------------------------
// DLL load notification
// ---------------------------------------------------------------------------
typedef struct {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR  Buffer;
} MY_UNICODE_STRING;

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

static VOID NTAPI DllNotify(ULONG reason, const LDR_DLL_NOTIFICATION_DATA *data, PVOID ctx)
{
    (void)ctx;
    if (reason != LDR_DLL_NOTIFICATION_REASON_LOADED) return;
    if (!data || !data->Loaded.BaseDllName || !data->Loaded.BaseDllName->Buffer) return;
    const wchar_t *nm = data->Loaded.BaseDllName->Buffer;
    if (_wcsicmp(nm, L"GameAssembly.dll") != 0) return;
    LogA("DLLNOTIFY: GameAssembly base=%p", data->Loaded.DllBase);
    PatchGameAssembly(data->Loaded.DllBase);
}

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
    LogA("BOOTSTRAP: LoadLibrary(coreclr)=%p err=%lu (late=%lu)", (void *)cm, cm ? 0 : GetLastError(), GetLastError());
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
        LogA("WRAPPER: real_il2cpp_init NULL");
    }
    if (g_mode_full) Bootstrap();
    LogA("WRAPPER: returning %d", r);
    return r;
}

// ---------------------------------------------------------------------------
static DWORD WINAPI Watchdog(LPVOID p)
{
    (void)p;
    typedef UINT(WINAPI *tTBP)(UINT);
    HMODULE wm = LoadLibraryW(L"winmm.dll");
    if (wm) { tTBP tbp = (tTBP)GetProcAddress(wm, "timeBeginPeriod"); if (tbp) tbp(1); }

    DWORD start = GetTickCount();
    DWORD t0 = start;
    // tight phase: is GameAssembly mapped yet? poll at ~1 ms for up to 60 s
    while (!g_eat_patched && GetTickCount() - start < 60000) {
        HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
        if (ga) {
            LogA("POLL: GameAssembly appeared at %p (t=%lums)", (void *)ga, GetTickCount() - t0);
            PatchGameAssembly(ga);
            break;
        }
        Sleep(1);
    }

    for (;;) {
        Sleep(1000);
        DWORD el = GetTickCount() - start;
        LogA("WATCH: t=%lums gpa=%ld gpa_il2cpp=%ld wrapper=%ld eat_patched=%ld boot=%ld done=%ld",
             el, g_gpa_calls, g_il2cpp_hits, g_wrapper_calls, g_eat_patched, g_bootstrap_started, g_bootstrap_done);
        if (g_fallback_ms && el > g_fallback_ms && !g_wrapper_calls) {
            LogA("FALLBACK: hook never fired after %lums, bootstrapping directly", el);
            g_fallback_ms = 0;
            Bootstrap();
        }
    }
    return 0;
}

// All real work happens here, NOT in DllMain: LdrRegisterDllNotification and
// other loader APIs take the loader lock and would deadlock if called while
// DllMain holds it (measured: remote LoadLibraryW thread hung forever).
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
            if (s) { *s = 0; _snwprintf_s(logpath, MAX_PATH, _TRUNCATE, L"%s\\probe4.txt", self); }
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
    wchar_t o[32] = L"0";
    GetEnvironmentVariableW(L"KIMI_PATCH_GPSHELL", o, 32); g_patch_gpshell = (_wtoi(o) != 0);
    GetEnvironmentVariableW(L"KIMI_USE_NOTIFY", o, 32);     g_use_notify = (_wtoi(o) != 0);

    GetModuleFileNameW(NULL, g_exepath, MAX_PATH);
    lstrcpyW(g_gamedir, g_exepath);
    { wchar_t *s = wcsrchr(g_gamedir, L'\\'); if (s) *s = 0; }

    LogA("");
    LogA("===== PROBE4 attach pid=%lu tid=%lu mode=%s fallback=%lums =====",
         GetCurrentProcessId(), GetCurrentThreadId(), g_mode_full ? "full" : "log", g_fallback_ms);

    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    real_GPA = (tGPA)GetProcAddress(k32, "GetProcAddress");

    HMODULE shell = GetModuleHandleW(L"gpShell.dll");
    if (g_patch_gpshell && shell && real_GPA) {
        int ok = PatchIatGpa(shell, (void *)real_GPA, (void *)det_GPA);
        LogA("gpShell=%p IAT GetProcAddress patch=%d", (void *)shell, ok);
    } else {
        LogA("gpShell=%p left untouched (KIMI_PATCH_GPSHELL=%d): measured fact - "
             "patching its IAT makes Unity abort with 'Failed to load il2cpp'",
             (void *)shell, g_patch_gpshell);
    }

    PVOID cookie = NULL;
    if (g_use_notify) {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        pLdrRegisterDllNotification reg =
            ntdll ? (pLdrRegisterDllNotification)GetProcAddress(ntdll, "LdrRegisterDllNotification") : NULL;
        NTSTATUS st = 0;
        if (reg) st = reg(0, DllNotify, NULL, &cookie);
        LogA("LdrRegisterDllNotification=%p status=0x%08lx cookie=%p", (void *)reg, (unsigned long)st, cookie);
    } else {
        LogA("DLL notification disabled; using tight polling instead");
    }

    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    if (ga) { LogA("GameAssembly already loaded: %p", (void *)ga); PatchGameAssembly(ga); }
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
