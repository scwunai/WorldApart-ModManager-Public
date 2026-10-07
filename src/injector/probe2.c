// probe2.c - in-process diagnostic probe (v2)
//
// Loaded by remote LoadLibraryW into the Unity process. Dumps, to an absolute
// log path taken from env KIMI_PROBE_LOG (fallback: <dir of this dll>\probe2.txt):
//   - module base addresses for main exe / gpShell / UnityPlayer / GameAssembly
//   - the IN-MEMORY import descriptor list of those modules: which DLL names the
//     loader-visible IAT actually references, and whether kernel32!GetProcAddress
//     is present in each module's IAT (this is exactly what doorstop's iat_hook
//     matches on)
//   - resolved il2cpp_init address + first bytes
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdarg.h>

static wchar_t g_log[MAX_PATH] = {0};
static FILE *g_f = NULL;

static void OpenLog(void)
{
    wchar_t env[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"KIMI_PROBE_LOG", env, MAX_PATH);
    if (n > 0 && n < MAX_PATH) lstrcpyW(g_log, env);
    if (!g_log[0]) {
        wchar_t self[MAX_PATH];
        DWORD m = GetModuleFileNameW((HMODULE)&OpenLog, self, MAX_PATH);
        if (m > 0 && m < MAX_PATH) {
            wchar_t *s = wcsrchr(self, L'\\');
            if (s) { *s = 0; _snwprintf_s(g_log, MAX_PATH, _TRUNCATE, L"%s\\probe2.txt", self); }
        }
    }
    if (g_log[0]) _wfopen_s(&g_f, g_log, L"a, ccs=UTF-8");
}

static void W(const wchar_t *fmt, ...)
{
    if (!g_f) return;
    va_list ap; va_start(ap, fmt);
    vfwprintf(g_f, fmt, ap);
    va_end(ap);
    fwprintf(g_f, L"\n");
    fflush(g_f);
}

#define RVA2PTR(t, base, rva) ((t)(((PCHAR)(base)) + (rva)))

static int ModuleHasGetProcAddress(HMODULE mod, void *needle, wchar_t *names, size_t names_cap)
{
    if (!mod) return -1;
    IMAGE_DOS_HEADER *mz = (PIMAGE_DOS_HEADER)mod;
    if (mz->e_magic != IMAGE_DOS_SIGNATURE) return -2;
    PIMAGE_NT_HEADERS nt = RVA2PTR(PIMAGE_NT_HEADERS, mz, mz->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return -2;
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    DWORD sz = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size;
    W(L"    import dir rva=0x%lx size=0x%lx", rva, sz);
    if (!rva) { W(L"    (no import directory)"); return -3; }
    IMAGE_IMPORT_DESCRIPTOR *imp = RVA2PTR(PIMAGE_IMPORT_DESCRIPTOR, mz, rva);
    int found = 0, total = 0;
    for (int i = 0; i < 64; i++) {
        if (!imp[i].Characteristics && !imp[i].FirstThunk && !imp[i].Name) break;
        char *nm = RVA2PTR(char *, mz, imp[i].Name);
        int hit = 0, cnt = 0;
        if (imp[i].FirstThunk) {
            void **thunk = RVA2PTR(void **, mz, imp[i].FirstThunk);
            for (int k = 0; k < 4096 && thunk[k]; k++) {
                cnt++;
                if (thunk[k] == needle) hit = 1;
            }
        }
        W(L"    IAT[%d] %hs funcs=%d%s", i, nm ? nm : "(null)", cnt, hit ? L"   <<< GetProcAddress HERE" : L"");
        if (nm && names && total < 8) {
            wchar_t tmp[64];
            MultiByteToWideChar(CP_ACP, 0, nm, -1, tmp, 64);
            lstrcatW(names, tmp); lstrcatW(names, L";"); total++;
        }
        if (hit) found = 1;
    }
    return found;
}

static void DumpModule(const wchar_t *label, HMODULE mod, void *gpa)
{
    if (!mod) { W(L"[%s] NOT LOADED", label); return; }
    wchar_t path[MAX_PATH] = L"";
    DWORD n = GetModuleFileNameW(mod, path, MAX_PATH);
    W(L"[%s] base=%p path=%s (GetModuleFileName rc=%lu)", label, (void *)mod, path, n);
    int r = ModuleHasGetProcAddress(mod, gpa, NULL, 0);
    W(L"    has GetProcAddress in IAT: %s (rc=%d)", r == 1 ? L"YES" : L"NO", r);
}

static void DumpLoadedModules(void)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) { W(L"  module snapshot failed err=%lu", GetLastError()); return; }
    MODULEENTRY32W me; me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) {
        do {
            W(L"  MOD %-40s base=%p size=%lu path=%s", me.szModule, me.modBaseAddr,
              (unsigned long)me.modBaseSize, me.szExePath);
        } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)hModule; (void)reserved;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;

    OpenLog();
    W(L"");
    W(L"===== PROBE2 attach pid=%lu tid=%lu cwd-log=%s =====",
      GetCurrentProcessId(), GetCurrentThreadId(), g_log);

    void *gpa = (void *)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetProcAddress");
    W(L"real kernel32!GetProcAddress = %p", gpa);

    HMODULE app = GetModuleHandleW(NULL);
    HMODULE shell = GetModuleHandleW(L"gpShell.dll");
    HMODULE up = GetModuleHandleW(L"UnityPlayer.dll");
    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");

    DumpModule(L"main-exe", app, gpa);
    DumpModule(L"gpShell", shell, gpa);
    DumpModule(L"UnityPlayer", up, gpa);
    DumpModule(L"GameAssembly", ga, gpa);
    DumpModule(L"kernel32", k32, gpa);

    if (ga) {
        void *ii = (void *)GetProcAddress(ga, "il2cpp_init");
        W(L"[il2cpp_init] = %p", ii);
        if (ii) {
            unsigned char b[16];
            memcpy(b, ii, 16);
            W(L"  first16 = %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x",
              b[0],b[1],b[2],b[3],b[4],b[5],b[6],b[7],b[8],b[9],b[10],b[11],b[12],b[13],b[14],b[15]);
        }
    }

    W(L"--- loaded modules ---");
    DumpLoadedModules();
    W(L"===== PROBE2 done =====");

    if (g_f) { fclose(g_f); g_f = NULL; }
    return TRUE;
}
