// ---------------------------------------------------------------------------
// ErwinNativeBridge  --  Phase A spike (v3: inline detour on GenerateAlterScript)
//
// v2 finding:  SetSynchronizeModelCallback + SetDisplayModelCallback never fire
//              during CC -> RD -> Alter Script. They're for other workflows.
// v3 strategy: inline-hook (detour) FEProcessor::GenerateAlterScript in EM_FEP.dll.
//              Erwin itself calls this when user clicks "Left Alter Script /
//              Schema Generation" on Resolve Differences. We capture the
//              pointers in-flight, forward to the original, log the result.
//
// Minimal naive x64 detour:
//   - Read first 14 bytes of target
//   - Safety check: abort if prologue contains a relative jump / call / jcc /
//     rip-relative addressing marker we can't relocate
//   - Allocate trampoline page: [saved 14 bytes][JMP abs target+14]
//   - Overwrite target first 14 bytes with JMP abs to our hook
//   - Our hook: log args, call trampoline, log rv
//
// v3 keeps the v2 SYNC/DISP chained callbacks as a belt-and-suspenders
// diagnostic (they proved they don't fire - but keep them in case they ever do).
// ---------------------------------------------------------------------------

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <stdint.h>

static void LogLine(const char* fmt, ...) {
    char path[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("TEMP", path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) strcpy_s(path, "C:\\tmp");
    strcat_s(path, "\\erwin-native-bridge.log");

    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") != 0 || !f) return;

    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(f, "[%02d:%02d:%02d.%03d] ",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list ap;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);

    fprintf(f, "\n");
    fclose(f);
}

static void LogBytes(const char* tag, const void* addr, size_t n) {
    char line[256];
    int p = sprintf_s(line, "%s %p:", tag, addr);
    for (size_t i = 0; i < n && p < (int)sizeof(line) - 4; i++) {
        p += sprintf_s(line + p, sizeof(line) - p, " %02X", ((const BYTE*)addr)[i]);
    }
    LogLine("%s", line);
}

// ---------------------------------------------------------------------------
// v2 callbacks kept as diagnostic (not expected to fire)
// ---------------------------------------------------------------------------
typedef bool(__cdecl* SyncCallbackFn)(void*, void*);
typedef void(__cdecl* SetSyncCallbackFn)(SyncCallbackFn);
typedef SyncCallbackFn(__cdecl* GetSyncCallbackFn)();
static SyncCallbackFn g_priorSync = nullptr;
static bool __cdecl SyncCallback(void* modelSet, void* actionSummary) {
    LogLine("[SYNC] fired. modelSet=%p actionSummary=%p", modelSet, actionSummary);
    if (g_priorSync) return g_priorSync(modelSet, actionSummary);
    return true;
}

typedef bool(__cdecl* DisplayCallbackFn)(void*);
typedef void(__cdecl* SetDisplayCallbackFn)(DisplayCallbackFn);
typedef DisplayCallbackFn(__cdecl* GetDisplayCallbackFn)();
static DisplayCallbackFn g_priorDisplay = nullptr;
static bool __cdecl DisplayCallback(void* modelSet) {
    LogLine("[DISP] fired. modelSet=%p", modelSet);
    if (g_priorDisplay) return g_priorDisplay(modelSet);
    return true;
}

// ---------------------------------------------------------------------------
// Minimal x64 inline detour
// ---------------------------------------------------------------------------

// Write 14-byte absolute JMP at `at` to `target`:
//   FF 25 00 00 00 00 [8-byte target]
static void WriteAbsJmp14(void* at, void* target) {
    BYTE* p = (BYTE*)at;
    p[0] = 0xFF; p[1] = 0x25;
    p[2] = 0x00; p[3] = 0x00; p[4] = 0x00; p[5] = 0x00;
    *(uint64_t*)(p + 6) = (uint64_t)target;
}

// Returns true if the 14 bytes at `p` are "safe to copy" for a trampoline:
// no relative jumps, no rip-relative addressing we can't relocate. This is
// conservative - if we say false we just won't install the hook.
static bool PrologueSafe(const BYTE* p, size_t n) {
    for (size_t i = 0; i < n; i++) {
        BYTE b = p[i];
        // short jmp/call rel8
        if (b == 0xEB /*JMP rel8*/ || b == 0xE8 /*CALL rel32*/ || b == 0xE9 /*JMP rel32*/) return false;
        // JCC short (70..7F)
        if (b >= 0x70 && b <= 0x7F) return false;
        // Two-byte JCC near (0F 80 .. 0F 8F)
        if (b == 0x0F && i + 1 < n) {
            BYTE b2 = p[i + 1];
            if (b2 >= 0x80 && b2 <= 0x8F) return false;
        }
        // RIP-relative MOD R/M: MOD=00 R/M=101 appears as 0x05 / 0x0D / 0x15 / ... in modrm
        // We can't easily distinguish modrm bytes from opcodes without a disassembler,
        // so we skip this check. MFC-compiled member function prologues on x64
        // typically don't contain rip-relative refs - they're sub rsp,X / mov [rsp],reg.
    }
    return true;
}

// Install an inline detour:
//   - Copies first 14 bytes of `target` into `trampolineOut` (allocated RWX)
//   - Appends JMP abs target+14 to trampolineOut
//   - Overwrites target's first 14 bytes with JMP abs to `hook`
static bool InstallInlineHook(void* target, void* hook, void** trampolineOut) {
    // Read target prologue for diagnostics
    BYTE saved[14];
    memcpy(saved, target, 14);
    LogBytes("[HOOK] target prologue bytes", target, 14);

    if (!PrologueSafe(saved, 14)) {
        LogLine("[HOOK] ABORT: target prologue contains relative jump/call; need a real disassembler.");
        return false;
    }

    // Allocate trampoline: 14 saved bytes + 14 byte abs JMP = 28 bytes
    void* tramp = VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) {
        LogLine("[HOOK] VirtualAlloc failed 0x%lX", GetLastError());
        return false;
    }
    memcpy(tramp, saved, 14);
    WriteAbsJmp14((BYTE*)tramp + 14, (BYTE*)target + 14);
    LogLine("[HOOK] trampoline at %p", tramp);

    // Overwrite target first 14 bytes with JMP abs to hook
    DWORD oldProt = 0;
    if (!VirtualProtect(target, 14, PAGE_EXECUTE_READWRITE, &oldProt)) {
        LogLine("[HOOK] VirtualProtect(RWX) failed 0x%lX", GetLastError());
        VirtualFree(tramp, 0, MEM_RELEASE);
        return false;
    }
    WriteAbsJmp14(target, hook);
    DWORD tmp;
    VirtualProtect(target, 14, oldProt, &tmp);
    FlushInstructionCache(GetCurrentProcess(), target, 14);
    LogLine("[HOOK] detour installed: %p -> %p", target, hook);

    *trampolineOut = tramp;
    return true;
}

// ---------------------------------------------------------------------------
// FEProcessor::GenerateAlterScript hook + FEProcessor::GetScript caller
//
//   GenerateAlterScript signature (x64 __thiscall == __cdecl with this first):
//     eFEPResult __cdecl FEProcessor::GenerateAlterScript(
//         FEProcessor* this,
//         GDMModelSetI* modelSet,
//         GDMActionSummary* actionSummary,
//         CWnd* parent,
//         bool showProgress);
//
//   GetScript returns std::vector<CString>& (the generated DDL lines).
//   On x64, reference return = pointer in RAX. MSVC std::vector binary
//   layout is {T* _Myfirst, T* _Mylast, T* _Myend} = 3 pointers, stable
//   across VS 2013 .. VS 2022. CString on x64 is a single char* pointer
//   to null-terminated data (ATL layout, also stable).
// ---------------------------------------------------------------------------
typedef int(__cdecl* GenerateAlterFn)(void* self, void* modelSet, void* actionSummary, void* parent, bool showProgress);
typedef void*(__cdecl* GetScriptFn)(void* self);   // returns &vector<CString>

static GenerateAlterFn g_origGenerateAlter = nullptr;
static GetScriptFn     g_getScript         = nullptr;

// Write the captured DDL lines to a separate file next to the diag log.
static void DumpScriptVector(void* vecPtr) {
    if (!vecPtr) { LogLine("[GA] GetScript returned null"); return; }

    // Read vector<CString>::{_Myfirst, _Mylast} at offsets 0, 8.
    char** begin = *(char***)vecPtr;
    char** end   = *(char***)((char*)vecPtr + 8);
    if (!begin || !end || end < begin) {
        LogLine("[GA] vector begin=%p end=%p - bad layout?", (void*)begin, (void*)end);
        return;
    }
    size_t count = (size_t)(end - begin);
    LogLine("[GA] script vector: begin=%p end=%p count=%zu", (void*)begin, (void*)end, count);

    // Build DDL output path: %TEMP%\erwin-alter-ddl-captured.sql
    char path[MAX_PATH];
    DWORD n = GetEnvironmentVariableA("TEMP", path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) strcpy_s(path, "C:\\tmp");
    strcat_s(path, "\\erwin-alter-ddl-captured.sql");

    FILE* f = nullptr;
    if (fopen_s(&f, path, "w") != 0 || !f) {
        LogLine("[GA] could not open %s for writing", path);
        return;
    }

    // Each element i: 8-byte CString = char* to null-terminated string.
    size_t totalChars = 0;
    size_t capped = count > 50000 ? 50000 : count;  // safety
    for (size_t i = 0; i < capped; i++) {
        const char* s = begin[i];
        if (!s) { fputs("(null)\n", f); continue; }
        fputs(s, f);
        fputc('\n', f);
        totalChars += strlen(s);
    }
    fclose(f);
    LogLine("[GA] wrote %zu lines / %zu chars to %s", capped, totalChars, path);

    // Also log first 3 lines inline so the diag log shows what was captured
    // without needing to open the SQL file.
    for (size_t i = 0; i < 3 && i < count; i++) {
        const char* s = begin[i];
        LogLine("[GA] line[%zu] = \"%s\"", i, s ? s : "(null)");
    }
}

static int __cdecl GenerateAlterHook(void* self, void* modelSet, void* actionSummary, void* parent, bool showProgress) {
    LogLine("[GA] ENTER self=%p modelSet=%p actionSummary=%p parent=%p show=%d",
        self, modelSet, actionSummary, parent, (int)showProgress);

    int rv = -1;
    if (g_origGenerateAlter) {
        __try {
            rv = g_origGenerateAlter(self, modelSet, actionSummary, parent, showProgress);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[GA] trampoline SEH 0x%08lX", GetExceptionCode());
        }
    } else {
        LogLine("[GA] no trampoline!");
    }
    LogLine("[GA] EXIT rv=%d", rv);

    // Phase B: after erwin's GenerateAlterScript succeeded, read the vector.
    if (rv == 0 && g_getScript && self) {
        __try {
            void* vec = g_getScript(self);
            LogLine("[GA] GetScript(self) = %p", vec);
            DumpScriptVector(vec);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[GA] GetScript SEH 0x%08lX", GetExceptionCode());
        }
    }

    return rv;
}

// ---------------------------------------------------------------------------
// Mangled symbols (dumpbin verified)
// ---------------------------------------------------------------------------
static const char* kSetSyncSym     = "?SetSynchronizeModelCallback@ECX@@SAXP6A_NPEAVGDMModelSetI@@PEAVGDMActionSummary@@@Z@Z";
static const char* kGetSyncSym     = "?SynchronizeModelCallback@ECX@@SAP6A_NPEAVGDMModelSetI@@PEAVGDMActionSummary@@@ZXZ";
static const char* kSetDisplaySym  = "?SetDisplayModelCallback@ECX@@SAXP6A_NPEAVGDMModelSetI@@@Z@Z";
static const char* kGetDisplaySym  = "?DisplayModelCallback@ECX@@SAP6A_NPEAVGDMModelSetI@@@ZXZ";
static const char* kGenAlterSym    = "?GenerateAlterScript@FEProcessor@@QEAA?AW4eFEPResult@@PEAVGDMModelSetI@@PEAVGDMActionSummary@@PEAVCWnd@@_N@Z";
static const char* kGetScriptSym   = "?GetScript@FEProcessor@@QEAAAEAV?$vector@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@V?$allocator@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@@std@@@std@@XZ";

extern "C" __declspec(dllexport) int __cdecl InstallHook(void) {
    LogLine("====== InstallHook() v3 in PID %lu ======", GetCurrentProcessId());

    // ----- v2 chained callbacks (kept as diagnostic) ----------------------
    HMODULE ecx = GetModuleHandleW(L"EM_ECX.dll");
    if (!ecx) ecx = LoadLibraryW(L"EM_ECX.dll");
    if (!ecx) {
        LogLine("ERR: EM_ECX.dll not found (GetLastError=0x%lX)", GetLastError());
    } else {
        LogLine("EM_ECX.dll handle = %p", (void*)ecx);
        SetSyncCallbackFn setSync = (SetSyncCallbackFn)GetProcAddress(ecx, kSetSyncSym);
        GetSyncCallbackFn getSync = (GetSyncCallbackFn)GetProcAddress(ecx, kGetSyncSym);
        if (setSync) {
            if (getSync) g_priorSync = getSync();
            setSync(&SyncCallback);
            LogLine("[SYNC] installed (prior=%p, ours=%p)", (void*)g_priorSync, (void*)&SyncCallback);
        }
        SetDisplayCallbackFn setDisp = (SetDisplayCallbackFn)GetProcAddress(ecx, kSetDisplaySym);
        GetDisplayCallbackFn getDisp = (GetDisplayCallbackFn)GetProcAddress(ecx, kGetDisplaySym);
        if (setDisp) {
            if (getDisp) g_priorDisplay = getDisp();
            setDisp(&DisplayCallback);
            LogLine("[DISP] installed (prior=%p, ours=%p)", (void*)g_priorDisplay, (void*)&DisplayCallback);
        }
    }

    // ----- v3 inline detour on GenerateAlterScript ------------------------
    HMODULE fep = GetModuleHandleW(L"EM_FEP.dll");
    if (!fep) fep = LoadLibraryW(L"EM_FEP.dll");
    if (!fep) {
        LogLine("ERR: EM_FEP.dll not found (GetLastError=0x%lX)", GetLastError());
        return 3;
    }
    LogLine("EM_FEP.dll handle = %p", (void*)fep);

    void* genAlter = GetProcAddress(fep, kGenAlterSym);
    if (!genAlter) {
        LogLine("ERR: GenerateAlterScript export missing");
        return 4;
    }
    LogLine("GenerateAlterScript addr = %p", genAlter);

    // Resolve GetScript (Phase B - used from inside the detour after
    // erwin's GenerateAlterScript succeeds).
    g_getScript = (GetScriptFn)GetProcAddress(fep, kGetScriptSym);
    if (!g_getScript) {
        LogLine("WARN: GetScript export missing - DDL dump will be skipped");
    } else {
        LogLine("GetScript addr = %p", (void*)g_getScript);
    }

    void* tramp = nullptr;
    if (!InstallInlineHook(genAlter, (void*)&GenerateAlterHook, &tramp)) {
        LogLine("ERR: InstallInlineHook(GenerateAlterScript) failed");
        return 5;
    }
    g_origGenerateAlter = (GenerateAlterFn)tramp;
    LogLine("OK: GenerateAlterScript detour armed. trampoline=%p", tramp);
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl UninstallHook(void) {
    // For the spike we intentionally leave the detour installed for the process
    // lifetime. Restoring the original bytes mid-flight risks a race if erwin
    // is currently in GenerateAlterScript. A rebuild/inject cycle is cheap.
    LogLine("UninstallHook: no-op (detour intentionally left armed).");
    return 0;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        LogLine("DllMain DLL_PROCESS_ATTACH in PID %lu", GetCurrentProcessId());
    } else if (reason == DLL_PROCESS_DETACH) {
        LogLine("DllMain DLL_PROCESS_DETACH");
    }
    return TRUE;
}
