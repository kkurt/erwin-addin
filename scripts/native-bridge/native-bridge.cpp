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
#include <set>
#include <string.h>

static void GetLogPath(char* out, size_t outSize) {
    DWORD n = GetEnvironmentVariableA("TEMP", out, (DWORD)outSize);
    if (n == 0 || n >= outSize) strcpy_s(out, outSize, "C:\\tmp");
    strcat_s(out, outSize, "\\erwin-native-bridge.log");
}

// Called once at DllMain-attach to truncate the log so each new erwin
// process starts with a clean file.
static void TruncateLog(void) {
    char path[MAX_PATH];
    GetLogPath(path, sizeof(path));
    FILE* f = nullptr;
    if (fopen_s(&f, path, "w") == 0 && f) fclose(f);
}

static void LogLine(const char* fmt, ...) {
    char path[MAX_PATH];
    GetLogPath(path, sizeof(path));

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

// Forward declaration so PrologueSafe can walk instructions by length.
static size_t InstrLen_x64(const BYTE* p);

// Boundary-aware: only flags relative-branch opcodes at instruction START.
// Mid-instruction ModR/M bytes like 0x74 (part of [rsp+0x18]) no longer
// false-positive as "JE rel8".
static bool PrologueSafe(const BYTE* p, size_t n) {
    size_t i = 0;
    while (i < n) {
        BYTE b = p[i];
        if (b == 0xEB || b == 0xE8 || b == 0xE9) return false;   // JMP rel8/CALL/JMP rel32 at boundary
        if (b >= 0x70 && b <= 0x7F) return false;                // JCC rel8 at boundary
        if (b == 0x0F && i + 1 < n) {
            BYTE b2 = p[i + 1];
            if (b2 >= 0x80 && b2 <= 0x8F) return false;          // JCC rel32 at boundary
        }
        size_t len = InstrLen_x64(p + i);
        if (len == 0) return false;    // unknown opcode -> unsafe
        i += len;
    }
    return true;
}

// Minimal x64 instruction length disassembler for common MFC prologue ops.
// Returns number of bytes for the instruction at `p`, or 0 on unknown.
// Only handles the short list we've actually seen in erwin's FEProcessor
// exports. If we hit an unknown byte we return 0 and the caller aborts.
static size_t InstrLen_x64(const BYTE* p) {
    BYTE b0 = p[0];
    // REX prefix (0x40..0x4F) - consume and recurse
    if (b0 >= 0x40 && b0 <= 0x4F) {
        size_t sub = InstrLen_x64(p + 1);
        return sub ? sub + 1 : 0;
    }
    switch (b0) {
        // SUB r/m64, imm8   (REX.W + 83 /5 ib)  -> with REX = 4 bytes (REX already consumed)
        // Without REX: 83 /r ib = 3 bytes; with REX we recursed so this branch is already +1.
        case 0x83: return 3;                       // 83 /X ib (3 bytes)
        case 0x81: return 6;                       // 81 /X id (6 bytes)
        // MOV r, r/m or MOV r/m, r or LEA r, m (same ModR/M encoding)
        case 0x89: case 0x8B: case 0x8D: {
            BYTE modrm = p[1];
            BYTE mod = modrm >> 6;
            BYTE rm  = modrm & 7;
            size_t sz = 2; // opcode + modrm
            if (mod != 3 && rm == 4) sz += 1;      // SIB
            if (mod == 1) sz += 1;                 // disp8
            else if (mod == 2) sz += 4;            // disp32
            else if (mod == 0 && rm == 5) {
                // RIP-relative disp32 — we CANNOT naively copy this to the
                // trampoline because the disp32 is relative to RIP at the
                // TRAMPOLINE address, not the original. Returning 0 here makes
                // AlignedCopyLen bail out -> InstallInlineHook aborts rather
                // than crashing erwin when the relocated MOV/LEA dereferences
                // garbage. Relocating would require rewriting the disp32 which
                // we don't implement. (Discovered the hard way via erwin crash.)
                return 0;
            }
            return sz;
        }
        // MOV [r/m], imm8  -> C6 /0 ib         (2 + sib/disp + 1)
        case 0xC6: {
            BYTE modrm = p[1];
            BYTE mod = modrm >> 6;
            BYTE rm  = modrm & 7;
            size_t sz = 2;
            if (mod != 3 && rm == 4) sz += 1;      // SIB
            if (mod == 1) sz += 1;                 // disp8
            else if (mod == 2) sz += 4;
            sz += 1;                               // imm8
            return sz;
        }
        // XOR r32, r/m32 (33 /r)
        case 0x33: case 0x31: {
            BYTE modrm = p[1];
            BYTE mod = modrm >> 6;
            BYTE rm  = modrm & 7;
            size_t sz = 2;
            if (mod != 3 && rm == 4) sz += 1;
            if (mod == 1) sz += 1;
            else if (mod == 2) sz += 4;
            return sz;
        }
        // PUSH r (50..57), POP r (58..5F)
        case 0x50: case 0x51: case 0x52: case 0x53:
        case 0x54: case 0x55: case 0x56: case 0x57:
        case 0x58: case 0x59: case 0x5A: case 0x5B:
        case 0x5C: case 0x5D: case 0x5E: case 0x5F:
            return 1;
        default:
            return 0;   // unknown - caller aborts
    }
}

// Compute the smallest instruction-aligned prefix length >= minBytes.
// Returns 0 if the disassembler hit an unknown opcode before reaching minBytes.
static size_t AlignedCopyLen(const BYTE* p, size_t minBytes) {
    size_t total = 0;
    while (total < minBytes) {
        size_t ilen = InstrLen_x64(p + total);
        if (ilen == 0) return 0;
        total += ilen;
    }
    return total;
}

// Install an inline detour. Computes instruction-boundary-aware copy size,
// allocates a trampoline (saved bytes + JMP to target+copySize), then
// overwrites target's first 14 bytes with JMP to hook. Returns false if
// the prologue is unsafe or we can't decode enough bytes.
static bool InstallInlineHook(void* target, void* hook, void** trampolineOut) {
    BYTE* t = (BYTE*)target;
    LogBytes("[HOOK] target prologue bytes", target, 20);

    if (!PrologueSafe(t, 20)) {
        LogLine("[HOOK] ABORT: prologue contains relative jump/call (need disassembler).");
        return false;
    }

    size_t copyLen = AlignedCopyLen(t, 14);
    if (copyLen == 0) {
        LogLine("[HOOK] ABORT: could not decode prologue to a 14-byte boundary.");
        return false;
    }
    LogLine("[HOOK] aligned copy length = %zu bytes", copyLen);

    void* tramp = VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) {
        LogLine("[HOOK] VirtualAlloc failed 0x%lX", GetLastError());
        return false;
    }
    memcpy(tramp, t, copyLen);
    WriteAbsJmp14((BYTE*)tramp + copyLen, t + copyLen);
    LogLine("[HOOK] trampoline at %p (copy=%zu + 14 jmp)", tramp, copyLen);

    // Overwrite target's first 14 bytes with JMP abs to hook.
    // (copyLen may be >14; that's fine - we only overwrite the JMP area.
    // Any bytes at target+14..target+copyLen are untouched, and the hook
    // JMP will redirect before they're executed.)
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

// GenerateFEScript is the SCAPI-internal forward-engineer entry point
//   eFEPResult FEProcessor::GenerateFEScript(GDMModelSetI* ms, CWnd* parent);
// When our managed addin calls ISCPersistenceUnit.FEModel_DDL(...), erwin
// ultimately lands here with the PU's GDMModelSetI* in RDX. We use that
// to solve the "SCAPI -> GDMModelSetI*" opacity problem: one controlled
// FEModel_DDL call, our detour captures the pointer, we cache it.
typedef int(__cdecl* GenerateFeFn)(void* self, void* modelSet, void* parent);

static GenerateAlterFn g_origGenerateAlter = nullptr;
static GetScriptFn     g_getScript         = nullptr;
static GenerateFeFn    g_origGenerateFe    = nullptr;

// Thread-safe latest captured GDMModelSetI* pointer (from GenerateFEScript detour).
static volatile LONG64 g_lastCapturedModelSet = 0;
static volatile LONG   g_feCallCount          = 0;

// Most-recent DDL captured from FEProcessor::GenerateAlterScript + GetScript.
// Populated by GenerateAlterHook after a successful alter-script generation.
// Ownership: malloc'd buffer, protected by g_ddlLock; consumers obtain it
// via ConsumeLastCapturedDdl() which atomically swaps pointer out.
static CRITICAL_SECTION g_ddlLock;
static char*           g_lastCapturedDdl = nullptr;   // protected by g_ddlLock
static bool            g_ddlLockInited   = false;

static void EnsureDdlLockInit() {
    if (!g_ddlLockInited) {
        InitializeCriticalSection(&g_ddlLock);
        g_ddlLockInited = true;
    }
}

static void StoreCapturedDdl(char* ddl /* takes ownership */) {
    EnsureDdlLockInit();
    EnterCriticalSection(&g_ddlLock);
    if (g_lastCapturedDdl) { free(g_lastCapturedDdl); g_lastCapturedDdl = nullptr; }
    g_lastCapturedDdl = ddl;
    LeaveCriticalSection(&g_ddlLock);
}

// Forward declaration - definition lives further down in FAZ 2 section.
static char* ConcatScriptVector(void* vecPtr);

// ---------------------------------------------------------------------------
// FEProcessor::GenerateFEScript detour - silent pointer capture
// ---------------------------------------------------------------------------
static int __cdecl GenerateFeHook(void* self, void* modelSet, void* parent) {
    LONG c = InterlockedIncrement(&g_feCallCount);
    InterlockedExchange64(&g_lastCapturedModelSet, (LONG64)modelSet);
    if (c <= 3) {
        LogLine("[FE] GenerateFEScript #%ld: self=%p modelSet=%p parent=%p (captured)",
            c, self, modelSet, parent);
    }
    int rv = -1;
    if (g_origGenerateFe) {
        __try {
            rv = g_origGenerateFe(self, modelSet, parent);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[FE] trampoline SEH 0x%08lX", GetExceptionCode());
        }
    }
    return rv;
}

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
    // Also serves as our ModelSet capture point - GA fires every time FEModel_DDL
    // runs (internally it calls GenerateAlterScript with actionSummary=NULL).
    // GenerateFEScript would be cleaner but its prologue contains a relative
    // CALL we can't safely relocate without a full disassembler.
    if (modelSet) {
        InterlockedExchange64((volatile LONG64*)&g_lastCapturedModelSet, (LONG64)modelSet);
    }
    LogLine("[GA] ENTER self=%p modelSet=%p actionSummary=%p parent=%p show=%d",
        self, modelSet, actionSummary, parent, (int)showProgress);

    // --- STACK TRACE: who called us? Whoever did is holding the actionSummary
    // pointer, which means they built or received it. Following the chain up
    // tells us where actionSummary originates. Log top 12 frames as
    // module!RVA so we can dumpbin them later.
    {
        void* frames[12];
        USHORT captured = CaptureStackBackTrace(0, 12, frames, nullptr);
        for (USHORT i = 0; i < captured; i++) {
            HMODULE h = nullptr;
            if (GetModuleHandleExW(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    (LPCWSTR)frames[i], &h) && h)
            {
                wchar_t path[MAX_PATH];
                DWORD n = GetModuleFileNameW(h, path, MAX_PATH);
                if (n > 0) {
                    // Just the filename portion
                    const wchar_t* name = wcsrchr(path, L'\\');
                    name = name ? name + 1 : path;
                    char ascii[MAX_PATH];
                    WideCharToMultiByte(CP_ACP, 0, name, -1, ascii, MAX_PATH, nullptr, nullptr);
                    uintptr_t rva = (uintptr_t)frames[i] - (uintptr_t)h;
                    LogLine("[GA-STACK] #%u  %s + 0x%llX  (abs=%p)",
                        (unsigned)i, ascii, (unsigned long long)rva, frames[i]);
                } else {
                    LogLine("[GA-STACK] #%u  <unnamed module>  abs=%p", (unsigned)i, frames[i]);
                }
            } else {
                LogLine("[GA-STACK] #%u  <no module>  abs=%p", (unsigned)i, frames[i]);
            }
        }
    }

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
    // We write to two places:
    //   1. %TEMP%\erwin-alter-ddl-captured.sql (for diagnostic)
    //   2. g_lastCapturedDdl in-memory buffer (for ConsumeLastCapturedDdl)
    if (rv == 0 && g_getScript && self) {
        __try {
            void* vec = g_getScript(self);
            LogLine("[GA] GetScript(self) = %p", vec);
            DumpScriptVector(vec);
            char* ddl = ConcatScriptVector(vec);
            if (ddl) {
                LogLine("[GA] stored captured DDL (%zu chars) for managed consumer", strlen(ddl));
                StoreCapturedDdl(ddl);   // takes ownership
            } else {
                LogLine("[GA] ConcatScriptVector returned null - not storing");
            }
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
static const char* kGenFeSym       = "?GenerateFEScript@FEProcessor@@QEAA?AW4eFEPResult@@PEAVGDMModelSetI@@PEAVCWnd@@@Z";
static const char* kGetScriptSym   = "?GetScript@FEProcessor@@QEAAAEAV?$vector@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@V?$allocator@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@@std@@@std@@XZ";

// Forward decl so InstallHook() can call it before its definition further down.
static void InstallObserverHooks(void);

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

    // ----- Faz 1: detour GenerateFEScript for silent ModelSet capture -----
    void* genFe = GetProcAddress(fep, kGenFeSym);
    if (!genFe) {
        LogLine("WARN: GenerateFEScript export missing - pointer capture disabled");
    } else {
        LogLine("GenerateFEScript addr = %p", genFe);
        void* trampFe = nullptr;
        if (!InstallInlineHook(genFe, (void*)&GenerateFeHook, &trampFe)) {
            LogLine("WARN: GenerateFEScript detour NOT installed (unsafe prologue)");
        } else {
            g_origGenerateFe = (GenerateFeFn)trampFe;
            LogLine("OK: GenerateFEScript detour armed. trampoline=%p", trampFe);
        }
    }

    // ----- Faz A: install observer detours eagerly -----
    // Critical: FEWPageOptions ctor hook lives in InstallObserverHooks() and is
    // required for OpenAlterScriptWizardHidden to detect that the wizard has
    // been constructed. Without it, the wizard opens visibly and our 15s
    // ctor-wait times out. Install at startup instead of waiting for a manual
    // trigger from the debug panel.
    InstallObserverHooks();

    return 0;
}

// ---------------------------------------------------------------------------
// Faz 1 exports: ModelSet pointer access
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport) void* __cdecl GetLastCapturedModelSet(void) {
    return (void*)InterlockedCompareExchange64(
        (volatile LONG64*)&g_lastCapturedModelSet, 0, 0);
}

extern "C" __declspec(dllexport) void __cdecl ResetCapturedModelSet(void) {
    InterlockedExchange64((volatile LONG64*)&g_lastCapturedModelSet, 0);
    LogLine("[FE] capture reset");
}

// ===========================================================================
// FAZ 2: Silent Alter DDL pipeline (no UI, pure native)
//
// Chain erwin's own natives:
//   MCXMartModelUtilities::PrepareServerModelSet(clientMs, &as1)   -> serverMs
//   MCXMartModelUtilities::InitializeClientActionSummary(clientMs, &as2)
//   MCXInvokeCompleteCompare cc(serverMs, clientMs, as1, as2);     // stack buf
//   cc.Execute(clientMs);                                          // populates as2
//   FEProcessor fep;                                               // stack buf
//   fep.GenerateAlterScript(clientMs, as2, null, false);
//   ddlLines = fep.GetScript();                                    // vector<CString>&
//
// All pointers except the input clientMs are erwin-owned internals; we leave
// them alone. Buffers for cc/fep are our own, so we ctor + use + dtor + free.
// ===========================================================================

// Mangled symbols (dumpbin verified in EM_MCX.dll and EM_FEP.dll)
static const char* kPrepareServerSym =
    "?PrepareServerModelSet@MCXMartModelUtilities@@SAPEAVGDMModelSetI@@PEAV2@AEAPEAVGDMActionSummary@@@Z";
static const char* kInitClientAsSym  =
    "?InitializeClientActionSummary@MCXMartModelUtilities@@SA_NPEAVGDMModelSetI@@AEAPEAVGDMActionSummary@@@Z";
static const char* kMcxCtorSym       =
    "??0MCXInvokeCompleteCompare@@QEAA@PEAVGDMModelSetI@@0PEAVGDMActionSummary@@1@Z";
static const char* kMcxDtorSym       =
    "??1MCXInvokeCompleteCompare@@UEAA@XZ";
static const char* kMcxExecuteSym    =
    "?Execute@MCXInvokeCompleteCompare@@UEAA_NPEAVGDMModelSetI@@@Z";
static const char* kFepCtorSym       =
    "??0FEProcessor@@QEAA@XZ";
static const char* kFepDtorSym       =
    "??1FEProcessor@@UEAA@XZ";
// ECXAPIStatePack silent-mode toggles - suppress erwin dialogs during
// internal Mart calls so our pipeline stays headless.
static const char* kActivateSilentSym =
    "?ActivateSilentMode@ECXAPIStatePack@@SAXH@Z";
static const char* kIsSilentSym       =
    "?IsSilentMode@ECXAPIStatePack@@SA_NXZ";
// Mart-state diagnostics: tell us whether PrepareServerModelSet can possibly succeed.
static const char* kDoesDirtySym      =
    "?DoesModelHaveUnsavedChanges@MCXMartModelUtilities@@SA_NPEAVGDMModelSetI@@@Z";
static const char* kGetMartVerIdSym   =
    "?GetMartVersionId@MCXMartModelUtilities@@SAHPEAVGDMModelSetI@@@Z";

typedef void* (__cdecl* PrepareServerFn)(void* clientMs, void** outAs);
typedef bool  (__cdecl* InitClientAsFn)(void* clientMs, void** outAs);
typedef void  (__cdecl* McxCtorFn)(void* self, void* serverMs, void* clientMs, void* as1, void* as2);
typedef void  (__cdecl* McxDtorFn)(void* self);
typedef bool  (__cdecl* McxExecuteFn)(void* self, void* clientMs);
typedef void  (__cdecl* FepCtorFn)(void* self);
typedef void  (__cdecl* FepDtorFn)(void* self);
typedef void  (__cdecl* ActivateSilentFn)(int flag);
typedef bool  (__cdecl* IsSilentFn)(void);
typedef bool  (__cdecl* DoesDirtyFn)(void* ms);
typedef int   (__cdecl* GetMartVerFn)(void* ms);

static PrepareServerFn  g_prepareServer = nullptr;
static InitClientAsFn   g_initClientAs  = nullptr;
static McxCtorFn        g_mcxCtor       = nullptr;
static McxDtorFn        g_mcxDtor       = nullptr;
static McxExecuteFn     g_mcxExecute    = nullptr;
static FepCtorFn        g_fepCtor       = nullptr;
static FepDtorFn        g_fepDtor       = nullptr;
static ActivateSilentFn g_activateSilent = nullptr;
static IsSilentFn       g_isSilent       = nullptr;
static DoesDirtyFn      g_doesDirty      = nullptr;
static GetMartVerFn     g_getMartVer     = nullptr;
static bool             g_faz2Ready      = false;

static bool ResolveFaz2Symbols() {
    if (g_faz2Ready) return true;
    HMODULE mcx = GetModuleHandleW(L"EM_MCX.dll");
    if (!mcx) mcx = LoadLibraryW(L"EM_MCX.dll");
    HMODULE fep = GetModuleHandleW(L"EM_FEP.dll");
    if (!fep) fep = LoadLibraryW(L"EM_FEP.dll");
    HMODULE ecx = GetModuleHandleW(L"EM_ECX.dll");   // already loaded at InstallHook
    if (!mcx || !fep || !ecx) {
        LogLine("[F2] ResolveFaz2Symbols: EM_MCX=%p EM_FEP=%p EM_ECX=%p",
            (void*)mcx, (void*)fep, (void*)ecx);
        return false;
    }
    g_prepareServer = (PrepareServerFn)GetProcAddress(mcx, kPrepareServerSym);
    g_initClientAs  = (InitClientAsFn)GetProcAddress(mcx, kInitClientAsSym);
    g_mcxCtor       = (McxCtorFn)GetProcAddress(mcx, kMcxCtorSym);
    g_mcxDtor       = (McxDtorFn)GetProcAddress(mcx, kMcxDtorSym);
    g_mcxExecute    = (McxExecuteFn)GetProcAddress(mcx, kMcxExecuteSym);
    g_fepCtor       = (FepCtorFn)GetProcAddress(fep, kFepCtorSym);
    g_fepDtor       = (FepDtorFn)GetProcAddress(fep, kFepDtorSym);
    g_activateSilent = (ActivateSilentFn)GetProcAddress(ecx, kActivateSilentSym);
    g_isSilent       = (IsSilentFn)GetProcAddress(ecx, kIsSilentSym);
    g_doesDirty      = (DoesDirtyFn)GetProcAddress(mcx, kDoesDirtySym);
    g_getMartVer     = (GetMartVerFn)GetProcAddress(mcx, kGetMartVerIdSym);
    LogLine("[F2] PrepareServer=%p InitClientAs=%p McxCtor=%p McxDtor=%p McxExec=%p FepCtor=%p FepDtor=%p SilentAct=%p SilentIs=%p",
        (void*)g_prepareServer, (void*)g_initClientAs, (void*)g_mcxCtor,
        (void*)g_mcxDtor, (void*)g_mcxExecute, (void*)g_fepCtor, (void*)g_fepDtor,
        (void*)g_activateSilent, (void*)g_isSilent);
    // g_getScript was already resolved at InstallHook time. Silent toggles are optional.
    g_faz2Ready = g_prepareServer && g_initClientAs && g_mcxCtor && g_mcxDtor
               && g_mcxExecute && g_fepCtor && g_fepDtor && g_getScript;
    return g_faz2Ready;
}

// Concatenate a vector<CString>& (passed as void*) into a single malloc'd
// UTF-8 null-terminated string. Caller must free via FreeDdlBuffer.
static char* ConcatScriptVector(void* vecPtr) {
    if (!vecPtr) return nullptr;
    char** begin = *(char***)vecPtr;
    char** end   = *(char***)((char*)vecPtr + 8);
    if (!begin || !end || end < begin) return nullptr;
    size_t count = (size_t)(end - begin);
    size_t total = 1; // null terminator
    for (size_t i = 0; i < count; i++) if (begin[i]) total += strlen(begin[i]);
    char* buf = (char*)malloc(total);
    if (!buf) return nullptr;
    char* p = buf;
    for (size_t i = 0; i < count; i++) {
        if (!begin[i]) continue;
        size_t len = strlen(begin[i]);
        memcpy(p, begin[i], len);
        p += len;
    }
    *p = '\0';
    return buf;
}

// Internal worker - runs the full pipeline. Returns a malloc'd UTF-8 string
// on success, or nullptr on failure. Logs liberally for diagnostic.
static char* RunSilentAlterDdl(void* clientMs) {
    LogLine("===== [F2] RunSilentAlterDdl(clientMs=%p) =====", clientMs);
    if (!clientMs) { LogLine("[F2] clientMs is null"); return nullptr; }
    if (!ResolveFaz2Symbols()) {
        LogLine("[F2] symbol resolution failed");
        return nullptr;
    }

    void* as1 = nullptr;
    void* as2 = nullptr;
    void* serverMs = nullptr;
    char* ccBuf = (char*)_aligned_malloc(4096, 16);
    char* fepBuf = (char*)_aligned_malloc(4096, 16);
    char* result = nullptr;
    bool ccConstructed = false;
    bool fepConstructed = false;

    if (!ccBuf || !fepBuf) { LogLine("[F2] buffer alloc failed"); goto cleanup; }
    memset(ccBuf, 0, 4096);
    memset(fepBuf, 0, 4096);

    // Activate erwin's internal "silent mode" so utilities don't pop dialogs
    // (without this PrepareServerModelSet showed an "Unknown Error" MessageBox
    // when it hit an edge case).
    if (g_activateSilent) {
        __try { g_activateSilent(1); LogLine("[F2] silent mode ON"); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] ActivateSilentMode(1) SEH"); }
    }
    if (g_isSilent) {
        __try { LogLine("[F2] IsSilentMode() = %d", (int)g_isSilent()); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] IsSilentMode SEH"); }
    }

    // Diagnostic: tell us whether the model is Mart-tracked and dirty BEFORE
    // we attempt PrepareServerModelSet. If GetMartVersionId returns -1 or 0,
    // the model isn't a Mart client and the call will likely error.
    if (g_getMartVer) {
        __try {
            int vid = g_getMartVer(clientMs);
            LogLine("[F2] diag: GetMartVersionId = %d  (negative/zero => not a Mart client)", vid);
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] diag: GetMartVersionId SEH"); }
    }
    if (g_doesDirty) {
        __try {
            bool d = g_doesDirty(clientMs);
            LogLine("[F2] diag: DoesModelHaveUnsavedChanges = %d", (int)d);
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] diag: DoesModelHaveUnsavedChanges SEH"); }
    }

    __try {
        LogLine("[F2] step 1: PrepareServerModelSet...");
        serverMs = g_prepareServer(clientMs, &as1);
        LogLine("[F2]   serverMs=%p  as1=%p", serverMs, as1);
        if (!serverMs) { LogLine("[F2] PrepareServerModelSet returned null - Mart connection / version history missing?"); goto finally_block; }

        LogLine("[F2] step 2: InitializeClientActionSummary...");
        bool okInit = g_initClientAs(clientMs, &as2);
        LogLine("[F2]   rc=%d as2=%p", (int)okInit, as2);
        if (!okInit || !as2) { LogLine("[F2] InitializeClientActionSummary failed"); goto finally_block; }

        LogLine("[F2] step 3: MCXInvokeCompleteCompare ctor...");
        g_mcxCtor(ccBuf, serverMs, clientMs, as1, as2);
        ccConstructed = true;

        LogLine("[F2] step 4: MCXInvokeCompleteCompare::Execute...");
        bool ccOk = g_mcxExecute(ccBuf, clientMs);
        LogLine("[F2]   Execute -> %d", (int)ccOk);
        if (!ccOk) { LogLine("[F2] Execute returned false"); goto finally_block; }

        LogLine("[F2] step 5: FEProcessor ctor + GenerateAlterScript...");
        g_fepCtor(fepBuf);
        fepConstructed = true;
        int feRv = g_origGenerateAlter
            ? g_origGenerateAlter(fepBuf, clientMs, as2, nullptr, false)
            : -1;
        LogLine("[F2]   GenerateAlterScript rv=%d (0=OK)", feRv);
        if (feRv != 0) goto finally_block;

        LogLine("[F2] step 6: GetScript + concatenate...");
        void* vec = g_getScript(fepBuf);
        LogLine("[F2]   vec=%p", vec);
        result = ConcatScriptVector(vec);
        LogLine("[F2]   result=%p (len=%zu)", (void*)result, result ? strlen(result) : 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[F2] SEH 0x%08lX during pipeline", GetExceptionCode());
    }

finally_block:
    if (fepConstructed) { __try { g_fepDtor(fepBuf); } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] fepDtor SEH"); } }
    if (ccConstructed)  { __try { g_mcxDtor(ccBuf);  } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] mcxDtor SEH"); } }

cleanup:
    if (fepBuf) _aligned_free(fepBuf);
    if (ccBuf)  _aligned_free(ccBuf);
    // Restore silent mode so we don't leak state affecting other wizards.
    if (g_activateSilent) {
        __try { g_activateSilent(0); LogLine("[F2] silent mode OFF"); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2] ActivateSilentMode(0) SEH"); }
    }
    LogLine("===== [F2] done, result=%s =====", result ? "(text)" : "(null)");
    return result;
}

// ----- Exports -----
// ===========================================================================
// FAZ A-SPIKE: observer detours on MCX internal entry points so we can see
// what erwin's own CC wizard flow passes when the user triggers it from UI.
// ===========================================================================

// PrepareServerModelSet: GDMModelSetI*(GDMModelSetI* clientMs, GDMActionSummary** outAs)
static PrepareServerFn g_origPrepareServer = nullptr;
static void* __cdecl PrepareServerHook(void* clientMs, void** outAs) {
    LogLine("[OBS-PSM] ENTER clientMs=%p outAs=%p (outAs value=%p)",
        clientMs, (void*)outAs, outAs ? *outAs : nullptr);
    void* rv = nullptr;
    if (g_origPrepareServer) {
        __try { rv = g_origPrepareServer(clientMs, outAs); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-PSM] trampoline SEH 0x%08lX", GetExceptionCode()); }
    }
    LogLine("[OBS-PSM] EXIT rv(serverMs)=%p outAs value=%p",
        rv, outAs ? *outAs : nullptr);
    return rv;
}

// InitializeClientActionSummary: bool(GDMModelSetI* clientMs, GDMActionSummary** outAs)
static InitClientAsFn g_origInitClientAs = nullptr;
static bool __cdecl InitClientAsHook(void* clientMs, void** outAs) {
    LogLine("[OBS-ICA] ENTER clientMs=%p outAs=%p (in value=%p)",
        clientMs, (void*)outAs, outAs ? *outAs : nullptr);
    bool rv = false;
    if (g_origInitClientAs) {
        __try { rv = g_origInitClientAs(clientMs, outAs); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-ICA] trampoline SEH 0x%08lX", GetExceptionCode()); }
    }
    LogLine("[OBS-ICA] EXIT rv=%d outAs value=%p", (int)rv, outAs ? *outAs : nullptr);
    return rv;
}

// MCXInvokeCompleteCompare ctor: void(this, serverMs, clientMs, as1, as2)
static McxCtorFn g_origMcxCtor = nullptr;
static void __cdecl McxCtorHook(void* self, void* serverMs, void* clientMs, void* as1, void* as2) {
    LogLine("[OBS-MCX-CTOR] self=%p serverMs=%p clientMs=%p as1=%p as2=%p",
        self, serverMs, clientMs, as1, as2);
    if (g_origMcxCtor) {
        __try { g_origMcxCtor(self, serverMs, clientMs, as1, as2); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-MCX-CTOR] trampoline SEH 0x%08lX", GetExceptionCode()); }
    }
    LogLine("[OBS-MCX-CTOR] EXIT");
}

// MCXInvokeCompleteCompare::Execute: bool(this, clientMs)
static McxExecuteFn g_origMcxExecute = nullptr;
static bool __cdecl McxExecuteHook(void* self, void* clientMs) {
    LogLine("[OBS-MCX-EXEC] ENTER self=%p clientMs=%p", self, clientMs);
    bool rv = false;
    if (g_origMcxExecute) {
        __try { rv = g_origMcxExecute(self, clientMs); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-MCX-EXEC] trampoline SEH 0x%08lX", GetExceptionCode()); }
    }
    LogLine("[OBS-MCX-EXEC] EXIT rv=%d", (int)rv);
    return rv;
}

// Additional observer targets in EM_ECC.dll. These are the Resolve Differences
// "Apply differences" handlers and the ActionSummary builders we suspect
// erwin's real CC flow uses.
static const char* kApplyDiffRightSym =
    "?ApplyDifferencesToRight@CWizInterface@@SAHPEAVGDMModelSetI@@0@Z";
static const char* kApplyCCSilentSym =
    "?ApplyCCSilentMode@CWizInterface@@SAHPEAVGDMModelSetI@@0W4CCCompareLevelType_e@@W4CCOptionSetType_e@@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@@Z";
static const char* kEccBuildASSym =
    "?BuildActionSummary@EccClone@@AEAAPEAVGDMActionSummary@@AEAVGDMObject@@AEBV?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@W4EccClone_Action_e@1@@Z";
static const char* kEccExecASSym =
    "?ExecuteActionSummary@EccClone@@AEAA_NPEAVGDMActionSummary@@AEAVGDMObject@@1W4EccClone_Action_e@1@@Z";
static const char* kEccBuildTargetASSym =
    "?BuildTargetActionSummary@EccClipboard@@QEAAXPEAVGDMActionSummary@@PEAVGDMModelSetI@@VCPoint@@AEAVMCCBuilderIdMap@@1_N4@Z";

// Generic observer hooks for these signatures.
typedef int (__cdecl* ApplyDiffRightFn)(void* ms1, void* ms2);
static ApplyDiffRightFn g_origApplyDiffRight = nullptr;
static int __cdecl ApplyDiffRightHook(void* ms1, void* ms2) {
    LogLine("[OBS-ADR] ENTER ms1=%p ms2=%p", ms1, ms2);
    int rv = -1;
    if (g_origApplyDiffRight) { __try { rv = g_origApplyDiffRight(ms1, ms2); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-ADR] SEH"); } }
    LogLine("[OBS-ADR] EXIT rv=%d", rv);
    return rv;
}

// EccClone::BuildActionSummary - instance method: (this, obj, str, action_e)
typedef void* (__cdecl* EccBuildASFn)(void* self, void* obj, const void* str, int action);
static EccBuildASFn g_origEccBuildAS = nullptr;
static void* __cdecl EccBuildASHook(void* self, void* obj, const void* str, int action) {
    LogLine("[OBS-ECCBAS] ENTER self=%p obj=%p str=%p action=%d", self, obj, str, action);
    void* rv = nullptr;
    if (g_origEccBuildAS) { __try { rv = g_origEccBuildAS(self, obj, str, action); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-ECCBAS] SEH"); } }
    LogLine("[OBS-ECCBAS] EXIT rv(AS)=%p", rv);
    return rv;
}

// EccClone::ExecuteActionSummary - instance: (this, as, obj, obj2, action_e)
typedef bool (__cdecl* EccExecASFn)(void* self, void* as, void* obj, void* obj2, int action);
static EccExecASFn g_origEccExecAS = nullptr;
static bool __cdecl EccExecASHook(void* self, void* as, void* obj, void* obj2, int action) {
    LogLine("[OBS-ECCEAS] ENTER self=%p as=%p obj=%p obj2=%p action=%d", self, as, obj, obj2, action);
    bool rv = false;
    if (g_origEccExecAS) { __try { rv = g_origEccExecAS(self, as, obj, obj2, action); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-ECCEAS] SEH"); } }
    LogLine("[OBS-ECCEAS] EXIT rv=%d", (int)rv);
    return rv;
}

// Emit top-N caller frames (module+RVA) — for pinpointing AS builder.
static void LogStack(const char* tag) {
    void* frames[10];
    USHORT captured = CaptureStackBackTrace(0, 10, frames, nullptr);
    for (USHORT i = 0; i < captured; i++) {
        HMODULE h = nullptr;
        if (GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                (LPCWSTR)frames[i], &h) && h)
        {
            wchar_t path[MAX_PATH];
            DWORD n = GetModuleFileNameW(h, path, MAX_PATH);
            if (n > 0) {
                const wchar_t* name = wcsrchr(path, L'\\');
                name = name ? name + 1 : path;
                char ascii[MAX_PATH];
                WideCharToMultiByte(CP_ACP, 0, name, -1, ascii, MAX_PATH, nullptr, nullptr);
                uintptr_t rva = (uintptr_t)frames[i] - (uintptr_t)h;
                LogLine("%s #%u  %s + 0x%llX", tag, (unsigned)i, ascii, (unsigned long long)rva);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// CERwinFEData global state observers
//   ?SetActionSummary@CERwinFEData@@SAXPEAVGDMActionSummary@@@Z
//   ?SetModelSet@CERwinFEData@@SAXPEAVGDMModelSetI@@@Z
//   ?GetActionSummary@CERwinFEData@@SAPEAVGDMActionSummary@@XZ
//   ?ClearERwinFEData@CERwinFEData@@SAXXZ
// These four exports are the lifecycle hooks for the static globals that
// FEProcessor::GenerateAlterScript ultimately reads from.
// ---------------------------------------------------------------------------
typedef void (__cdecl* SetASFn)(void* as);
typedef void (__cdecl* SetMsFn)(void* ms);
typedef void* (__cdecl* GetASFn)(void);
typedef void (__cdecl* ClearAllFn)(void);

static SetASFn   g_origSetAS   = nullptr;
static SetMsFn   g_origSetMs   = nullptr;
static GetASFn   g_origGetAS   = nullptr;
static ClearAllFn g_origClear  = nullptr;

static void __cdecl SetASHook(void* as) {
    LogLine("[OBS-FED-SET-AS] called with as=%p", as);
    LogStack("[OBS-FED-SET-AS]");
    if (g_origSetAS) { __try { g_origSetAS(as); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-FED-SET-AS] SEH"); } }
}
static void __cdecl SetMsHook(void* ms) {
    LogLine("[OBS-FED-SET-MS] called with ms=%p", ms);
    LogStack("[OBS-FED-SET-MS]");
    if (g_origSetMs) { __try { g_origSetMs(ms); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-FED-SET-MS] SEH"); } }
}
static void* __cdecl GetASHook(void) {
    void* r = nullptr;
    if (g_origGetAS) { __try { r = g_origGetAS(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-FED-GET-AS] SEH"); } }
    // Log sparingly - called potentially often. Log first 5 reads per run.
    static LONG count = 0;
    LONG c = InterlockedIncrement(&count);
    if (c <= 5) {
        LogLine("[OBS-FED-GET-AS] #%ld returned %p", c, r);
        LogStack("[OBS-FED-GET-AS]");
    }
    return r;
}
static void __cdecl ClearHook(void) {
    LogLine("[OBS-FED-CLEAR] called");
    LogStack("[OBS-FED-CLEAR]");
    if (g_origClear) { __try { g_origClear(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[OBS-FED-CLEAR] SEH"); } }
}

static const char* kSetASSym    = "?SetActionSummary@CERwinFEData@@SAXPEAVGDMActionSummary@@@Z";
static const char* kSetMsSym    = "?SetModelSet@CERwinFEData@@SAXPEAVGDMModelSetI@@@Z";
static const char* kGetASSym    = "?GetActionSummary@CERwinFEData@@SAPEAVGDMActionSummary@@XZ";
static const char* kClearSym    = "?ClearERwinFEData@CERwinFEData@@SAXXZ";

// ---------------------------------------------------------------------------
// FEWPageOptions — the wizard "Options" page class.
// Its InvokePreviewStringOnlyCommand() method returns the DDL string directly
// when the user clicks Preview. No args other than `this`. If we capture a
// valid `this` pointer and the wizard stays alive (hidden), we can call this
// method ON DEMAND and get the DDL without navigating pages.
//
// x64 ABI notes:
//   ctor (member, __thiscall):
//     RCX=this, RDX=&WSFWizardBase, R8=&EouFEPARAM
//   Invoke (member, returns CString):
//     CString is non-trivially-copyable (has copy ctor, ref counted), so the
//     x64 ABI uses a hidden first argument for the return. Effective signature:
//     CString* Invoke(CString* retBuf /*RCX*/, void* self /*RDX*/);
//   CString layout on x64: single pointer (8 bytes) to heap-allocated data.
// ---------------------------------------------------------------------------
static const char* kFEWPageOptionsCtorSym =
    "??0FEWPageOptions@@QEAA@AEAVWSFWizardBase@@AEBUEouFEPARAM@@@Z";
// FEWPagePreviewEx — the Preview tab's page class. Hypothesis: inherits from
// FEWPageOptions so its `this` is what Invoke is called on when user clicks
// Preview. Ctor signature: (WSFWizardBase&, EouFEPARAM const&, PreviewState2_s*)
static const char* kFEWPagePreviewExCtorSym =
    "??0FEWPagePreviewEx@@QEAA@AEAVWSFWizardBase@@AEBUEouFEPARAM@@PEAUPreviewState2_s@FEW@@@Z";
static const char* kInvokePreviewSym =
    "?InvokePreviewStringOnlyCommand@FEWPageOptions@@QEAA?AV?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@XZ";

typedef void  (__cdecl* FEWCtorFn)(void* self, void* wsfBase, const void* feParam);
typedef void  (__cdecl* FEWPreviewExCtorFn)(void* self, void* wsfBase, const void* feParam, void* previewState);
typedef void* (__cdecl* InvokePreviewFn)(void* retBuf, void* self);

static FEWCtorFn           g_origFEWCtor             = nullptr;
static FEWPreviewExCtorFn  g_origFEWPreviewExCtor    = nullptr;
static InvokePreviewFn     g_origInvokePreview       = nullptr;
// Direct (un-hooked) address of InvokePreviewStringOnlyCommand - used by
// CallInvokePreviewOnCaptured to call into erwin without a detour trampoline.
static InvokePreviewFn     g_directInvokePreview     = nullptr;

// Stashed feParam template + wsfBase from first wizard open. Used for the
// GenerateAlterDdlStandalone experiment (construct FEWPageOptions without
// opening the wizard). Populated in FEWCtorHook on first ctor firing.
static BYTE g_stashedFeParam[128] = { 0 };
static bool g_stashedFeParamValid = false;
static void* g_stashedWsfBase = nullptr;

// Set by FEWCtorHook when we're in the middle of an auto-open sequence.
// Used to synchronize: the outer function polls this flag so it knows when
// erwin has started constructing the wizard (after SendInput).
static volatile LONG g_autoOpenCtorFired = 0;

// Most-recent hidden-wizard HWND. Published by either the polling loop in
// OpenAlterScriptWizardHidden OR (preferred, flash-free) the WinEvent hook
// that fires as soon as erwin creates the window.
static volatile LONG64 g_hiddenWizardHwnd = 0;

// Typedef for Invoke call (single-arg ABI, return-in-RAX). We don't actually
// use the return value — we just need the call to run so that erwin's
// internal GenerateAlterScript fires, and our GA detour captures the DDL
// into g_lastCapturedDdl. SEH after return is harmless.
typedef void* (__cdecl* InvokeFn)(void* self);
// Most-recent FEWPageOptions parametric instance (ctor hook). The "Options"
// page of the wizard.
static volatile LONG64 g_capturedFEWPO = 0;
// Most-recent FEWPagePreviewEx instance (ctor hook). HYPOTHESIS: this is a
// SUBCLASS of FEWPageOptions, so its `this` is the real argument to
// InvokePreviewStringOnlyCommand when user clicks Preview.
static volatile LONG64 g_capturedFEWPreviewEx = 0;

static void __cdecl FEWPreviewExCtorHook(void* self, void* wsfBase, const void* feParam, void* previewState) {
    InterlockedExchange64(&g_capturedFEWPreviewEx, (LONG64)self);
    LogLine("[FEW-PREVEX-CTOR] self=%p wsfBase=%p feParam=%p prevState=%p (cached as PreviewEx)",
        self, wsfBase, feParam, previewState);
    if (g_origFEWPreviewExCtor) {
        __try { g_origFEWPreviewExCtor(self, wsfBase, feParam, previewState); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[FEW-PREVEX-CTOR] trampoline SEH"); }
    }
}

static void __cdecl FEWCtorHook(void* self, void* wsfBase, const void* feParam) {
    InterlockedExchange64(&g_capturedFEWPO, (LONG64)self);
    InterlockedExchange(&g_autoOpenCtorFired, 1);  // signal outer polling
    // Stash the first 128 bytes of feParam + the wsfBase pointer so we can
    // later construct FEWPageOptions standalone without opening the wizard.
    if (feParam && !g_stashedFeParamValid) {
        __try {
            memcpy(g_stashedFeParam, feParam, sizeof(g_stashedFeParam));
            g_stashedWsfBase = wsfBase;
            g_stashedFeParamValid = true;
            LogLine("[FEW-CTOR] stashed feParam + wsfBase for standalone reconstruction");
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[FEW-CTOR] stash SEH"); }
    }
    LogLine("[FEW-CTOR] self=%p wsfBase=%p feParam=%p (cached)", self, wsfBase, feParam);
    // Peek first 40 bytes of feParam - might contain ModelSet / dirty flags
    if (feParam) {
        const BYTE* p = (const BYTE*)feParam;
        __try {
            LogLine("[FEW-CTOR] feParam[0..39]: "
                "%02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X "
                "%02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X "
                "%02X%02X%02X%02X %02X%02X%02X%02X",
                p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7],
                p[8],p[9],p[10],p[11],p[12],p[13],p[14],p[15],
                p[16],p[17],p[18],p[19],p[20],p[21],p[22],p[23],
                p[24],p[25],p[26],p[27],p[28],p[29],p[30],p[31],
                p[32],p[33],p[34],p[35],p[36],p[37],p[38],p[39]);
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[FEW-CTOR] feParam read SEH"); }
    }
    if (g_origFEWCtor) {
        __try { g_origFEWCtor(self, wsfBase, feParam); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[FEW-CTOR] trampoline SEH"); }
    }
}

static void* __cdecl InvokePreviewHook(void* retBuf, void* self) {
    LogLine("[IPS] ENTER retBuf=%p self=%p", retBuf, self);
    LogStack("[IPS]");
    void* rv = nullptr;
    if (g_origInvokePreview) {
        __try { rv = g_origInvokePreview(retBuf, self); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[IPS] trampoline SEH"); }
    }
    // After call, retBuf should contain a CString (single 8-byte pointer to heap data).
    if (retBuf) {
        __try {
            const char* dataPtr = *(const char**)retBuf;
            if (dataPtr) {
                size_t len = strlen(dataPtr);
                LogLine("[IPS] retBuf dataPtr=%p len=%zu", dataPtr, len);
                // First 250 chars preview
                char buf[260];
                size_t n = len < 250 ? len : 250;
                memcpy(buf, dataPtr, n);
                buf[n] = '\0';
                // Replace newlines for single-line logging
                for (size_t i = 0; i < n; i++) if (buf[i] == '\n' || buf[i] == '\r') buf[i] = ' ';
                LogLine("[IPS] preview(first %zu): \"%s\"", n, buf);
            } else {
                LogLine("[IPS] retBuf dataPtr is null");
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[IPS] retBuf read SEH"); }
    }
    LogLine("[IPS] EXIT rv=%p", rv);
    return rv;
}

static void InstallObserverHooks(void) {
    if (!ResolveFaz2Symbols()) {
        LogLine("[OBS] cannot install - Faz2 symbols not resolved");
        return;
    }
    LogLine("[OBS] installing observer detours...");
    void* tramp = nullptr;
    if (InstallInlineHook((void*)g_prepareServer, (void*)&PrepareServerHook, &tramp)) {
        g_origPrepareServer = (PrepareServerFn)tramp;
        LogLine("[OBS] PrepareServerModelSet hook ok");
    } else {
        LogLine("[OBS] PrepareServerModelSet hook FAILED (unsafe prologue)");
    }
    tramp = nullptr;
    if (InstallInlineHook((void*)g_initClientAs, (void*)&InitClientAsHook, &tramp)) {
        g_origInitClientAs = (InitClientAsFn)tramp;
        LogLine("[OBS] InitializeClientActionSummary hook ok");
    } else {
        LogLine("[OBS] InitializeClientActionSummary hook FAILED (unsafe prologue)");
    }
    tramp = nullptr;
    if (InstallInlineHook((void*)g_mcxCtor, (void*)&McxCtorHook, &tramp)) {
        g_origMcxCtor = (McxCtorFn)tramp;
        LogLine("[OBS] MCXInvokeCompleteCompare ctor hook ok");
    } else {
        LogLine("[OBS] MCXInvokeCompleteCompare ctor hook FAILED (unsafe prologue)");
    }
    tramp = nullptr;
    if (InstallInlineHook((void*)g_mcxExecute, (void*)&McxExecuteHook, &tramp)) {
        g_origMcxExecute = (McxExecuteFn)tramp;
        LogLine("[OBS] MCXInvokeCompleteCompare::Execute hook ok");
    } else {
        LogLine("[OBS] MCXInvokeCompleteCompare::Execute hook FAILED (unsafe prologue)");
    }

    // Additional observers in EM_ECC (Resolve Differences / clone helpers)
    HMODULE ecc = GetModuleHandleW(L"EM_ECC.dll");
    if (!ecc) ecc = LoadLibraryW(L"EM_ECC.dll");
    if (!ecc) {
        LogLine("[OBS] EM_ECC.dll not loaded - skipping RD observers");
        return;
    }

    void* applyDiff = GetProcAddress(ecc, kApplyDiffRightSym);
    if (applyDiff) {
        tramp = nullptr;
        if (InstallInlineHook(applyDiff, (void*)&ApplyDiffRightHook, &tramp)) {
            g_origApplyDiffRight = (ApplyDiffRightFn)tramp;
            LogLine("[OBS] ApplyDifferencesToRight hook ok");
        } else LogLine("[OBS] ApplyDifferencesToRight hook FAILED");
    }
    void* eccBuildAs = GetProcAddress(ecc, kEccBuildASSym);
    if (eccBuildAs) {
        tramp = nullptr;
        if (InstallInlineHook(eccBuildAs, (void*)&EccBuildASHook, &tramp)) {
            g_origEccBuildAS = (EccBuildASFn)tramp;
            LogLine("[OBS] EccClone::BuildActionSummary hook ok");
        } else LogLine("[OBS] EccClone::BuildActionSummary hook FAILED");
    }
    void* eccExecAs = GetProcAddress(ecc, kEccExecASSym);
    if (eccExecAs) {
        tramp = nullptr;
        if (InstallInlineHook(eccExecAs, (void*)&EccExecASHook, &tramp)) {
            g_origEccExecAS = (EccExecASFn)tramp;
            LogLine("[OBS] EccClone::ExecuteActionSummary hook ok");
        } else LogLine("[OBS] EccClone::ExecuteActionSummary hook FAILED");
    }
    // ApplyCCSilentMode - just log the symbol presence for now (signature is
    // more complex due to CString by-value). If the simpler hooks fire, we
    // won't need this.
    void* silentCc = GetProcAddress(ecc, kApplyCCSilentSym);
    LogLine("[OBS] ApplyCCSilentMode addr = %p (not yet hooked)", silentCc);

    // ----- CERwinFEData observers (EM_EOU.dll) -----
    HMODULE eou = GetModuleHandleW(L"EM_EOU.dll");
    if (!eou) eou = LoadLibraryW(L"EM_EOU.dll");
    if (!eou) {
        LogLine("[OBS] EM_EOU.dll not loaded - skipping FEData observers");
        return;
    }

    void* sym = GetProcAddress(eou, kSetASSym);
    if (sym) {
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&SetASHook, &tramp)) {
            g_origSetAS = (SetASFn)tramp;
            LogLine("[OBS] CERwinFEData::SetActionSummary hook ok");
        } else LogLine("[OBS] CERwinFEData::SetActionSummary hook FAILED");
    }
    sym = GetProcAddress(eou, kSetMsSym);
    if (sym) {
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&SetMsHook, &tramp)) {
            g_origSetMs = (SetMsFn)tramp;
            LogLine("[OBS] CERwinFEData::SetModelSet hook ok");
        } else LogLine("[OBS] CERwinFEData::SetModelSet hook FAILED");
    }
    sym = GetProcAddress(eou, kGetASSym);
    if (sym) {
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&GetASHook, &tramp)) {
            g_origGetAS = (GetASFn)tramp;
            LogLine("[OBS] CERwinFEData::GetActionSummary hook ok");
        } else LogLine("[OBS] CERwinFEData::GetActionSummary hook FAILED");
    }
    sym = GetProcAddress(eou, kClearSym);
    if (sym) {
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&ClearHook, &tramp)) {
            g_origClear = (ClearAllFn)tramp;
            LogLine("[OBS] CERwinFEData::ClearERwinFEData hook ok");
        } else LogLine("[OBS] CERwinFEData::ClearERwinFEData hook FAILED");
    }

    // ----- FEWPageOptions (wizard Options page) — key entry for Preview DDL -----
    sym = GetProcAddress(eou, kFEWPageOptionsCtorSym);
    if (sym) {
        LogLine("[OBS] FEWPageOptions ctor @ %p", sym);
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&FEWCtorHook, &tramp)) {
            g_origFEWCtor = (FEWCtorFn)tramp;
            LogLine("[OBS] FEWPageOptions ctor hook ok");
        } else LogLine("[OBS] FEWPageOptions ctor hook FAILED");
    } else LogLine("[OBS] FEWPageOptions ctor symbol missing");

    sym = GetProcAddress(eou, kInvokePreviewSym);
    if (sym) {
        // Prolog: 48 89 5C 24 18  57  48 81 EC 90 03 00 00  48 8B 05 DC CC 2F 00
        // Last instruction is mov rax,[rip+disp32] — RIP-relative, can't be
        // copied to a trampoline naively (disp32 is wrong at trampoline addr).
        // Our updated InstrLen_x64 now detects this and aborts hooks on it.
        // We STILL need this address to call Invoke directly, so cache it
        // without installing a detour.
        g_directInvokePreview = (InvokePreviewFn)sym;
        LogLine("[OBS] InvokePreviewStringOnlyCommand @ %p (direct-call, no detour)", sym);
    } else LogLine("[OBS] InvokePreviewStringOnlyCommand symbol missing");

    // FEWPagePreviewEx ctor — our hypothesis for the real Invoke target.
    sym = GetProcAddress(eou, kFEWPagePreviewExCtorSym);
    if (sym) {
        LogLine("[OBS] FEWPagePreviewEx ctor @ %p", sym);
        tramp = nullptr;
        if (InstallInlineHook(sym, (void*)&FEWPreviewExCtorHook, &tramp)) {
            g_origFEWPreviewExCtor = (FEWPreviewExCtorFn)tramp;
            LogLine("[OBS] FEWPagePreviewEx ctor hook ok");
        } else LogLine("[OBS] FEWPagePreviewEx ctor hook FAILED (unsafe prologue)");
    } else LogLine("[OBS] FEWPagePreviewEx ctor symbol missing");
}

// Exported: returns the most-recently-captured FEWPageOptions this-pointer
// (populated by the ctor hook). Null if no wizard has been opened yet.
extern "C" __declspec(dllexport) void* __cdecl GetCapturedFEWPageOptions(void) {
    return (void*)InterlockedCompareExchange64(&g_capturedFEWPO, 0, 0);
}

// Experimental: try to construct FEWPageOptions STANDALONE (without opening
// the wizard UI) and call Invoke on it. Uses the stashed wsfBase + feParam
// from a prior wizard session (captured in FEWCtorHook on first open).
// The idea: if FEWPageOptions ctor itself builds ActionSummary from
// feParam->modelSet, we can generate alter DDL purely programmatically.
//
// Caller supplies the CURRENT modelSet (our captured-via-FEModel_DDL pointer)
// so the AS reflects current dirty state, not the state at the time the
// feParam template was captured.
// ---------------------------------------------------------------------------
// Helpers for window enumeration.
// ---------------------------------------------------------------------------

struct FindMainCtx { HWND found; DWORD pid; };

static BOOL CALLBACK FindMainEnumProc(HWND hwnd, LPARAM lp) {
    auto ctx = (FindMainCtx*)lp;
    if (!IsWindowVisible(hwnd)) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;
    char cls[64];
    if (GetClassNameA(hwnd, cls, sizeof(cls)) > 0 && strcmp(cls, "XTPMainFrame") == 0) {
        ctx->found = hwnd;
        return FALSE;
    }
    return TRUE;
}

static HWND FindErwinMain(void) {
    FindMainCtx ctx = { nullptr, GetCurrentProcessId() };
    EnumWindows(FindMainEnumProc, (LPARAM)&ctx);
    return ctx.found;
}

struct EnumDlgCtx { std::set<HWND>* set; DWORD pid; };

static BOOL CALLBACK EnumDlgProc(HWND hwnd, LPARAM lp) {
    auto ctx = (EnumDlgCtx*)lp;
    if (!IsWindowVisible(hwnd)) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;
    ctx->set->insert(hwnd);
    return TRUE;
}

static std::set<HWND> EnumerateVisibleDialogs(void) {
    std::set<HWND> result;
    EnumDlgCtx ctx = { &result, GetCurrentProcessId() };
    EnumWindows(EnumDlgProc, (LPARAM)&ctx);
    return result;
}

// ---------------------------------------------------------------------------
// Auto-open the Alter Script wizard silently and hide it off-screen.
//
// How it works:
//   1. Find erwin's XTPMainFrame HWND
//   2. Snapshot currently-visible top-level dialogs
//   3. SendInput Ctrl+Alt+T (erwin's own shortcut for Actions > Alter Script)
//   4. Poll for a new top-level window whose title starts with
//      "Forward Engineer Alter Script" (max 3 seconds)
//   5. SetWindowPos to (-32000, -32000) the moment it's found — flash is
//      minimized but may be perceptible
//   6. Return the wizard HWND to the caller
//
// After this returns, the FEWCtor hook has fired and g_capturedFEWPO holds
// a valid FEWPageOptions pointer. Subsequent CallInvokePreviewOnCaptured
// calls produce alter DDL without any user interaction.
// ---------------------------------------------------------------------------
// Test whether a given HWND is our Alter Script wizard by title match.
static bool LooksLikeAlterScriptWizard(HWND hwnd) {
    char title[256];
    int n = GetWindowTextA(hwnd, title, sizeof(title));
    if (n <= 0) return false;
    return (strstr(title, "Alter Script") != nullptr ||
            strstr(title, "Schema Generation") != nullptr);
}

// Hide a wizard window aggressively: make it transparent (alpha=0) AND move
// off-screen. Using WS_EX_LAYERED with alpha=0 ensures it's not drawn at all
// even momentarily, so no flash is perceptible.
static void HideWizardAggressive(HWND hwnd) {
    LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    SetLayeredWindowAttributes(hwnd, 0, 0 /* fully transparent */, LWA_ALPHA);
    SetWindowPos(hwnd, NULL, -32000, -32000, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
}

// WinEvent callback: fires as soon as erwin creates a new window / shows one
// / changes its title. We use it to catch the Alter Script wizard the moment
// it's born, BEFORE the first paint - eliminating flash.
static void CALLBACK WizardWinEventCb(
    HWINEVENTHOOK /*hook*/, DWORD event, HWND hwnd,
    LONG idObject, LONG /*idChild*/, DWORD /*eventThread*/, DWORD /*eventTime*/)
{
    if (!hwnd) return;
    if (idObject != OBJID_WINDOW) return;
    if (g_hiddenWizardHwnd != 0) return;    // already hid one - don't re-hide

    // Must be same process
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) return;

    // Check title. At OBJECT_CREATE it might still be empty; retry at
    // OBJECT_SHOW / NAMECHANGE.
    if (LooksLikeAlterScriptWizard(hwnd)) {
        HideWizardAggressive(hwnd);
        InterlockedExchange64(&g_hiddenWizardHwnd, (LONG64)hwnd);
        LogLine("[WIN-EVT] hid wizard via WinEvent event=%lu hwnd=%p", event, hwnd);
    }
}

extern "C" __declspec(dllexport) void* __cdecl OpenAlterScriptWizardHidden(void) {
    HWND mainHwnd = FindErwinMain();
    if (!mainHwnd) { LogLine("[OPEN-WIZ] erwin main window not found"); return nullptr; }
    LogLine("[OPEN-WIZ] erwin main = %p", (void*)mainHwnd);

    // Baseline: what dialogs exist BEFORE we trigger the shortcut?
    auto before = EnumerateVisibleDialogs();
    LogLine("[OPEN-WIZ] baseline: %zu visible dialogs", before.size());

    // Reset signals so we can poll for the NEXT fire.
    InterlockedExchange(&g_autoOpenCtorFired, 0);
    InterlockedExchange64(&g_hiddenWizardHwnd, 0);

    // Subscribe to erwin's window create/show/name-change events. The callback
    // fires as soon as any matching window is born - faster than polling, and
    // importantly BEFORE the wizard paints, eliminating flash.
    HWINEVENTHOOK evHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
        nullptr, WizardWinEventCb,
        GetCurrentProcessId(), 0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS /* includes our own pid anyway */);
    // Note: WINEVENT_SKIPOWNPROCESS actually EXCLUDES same-process events; we
    // WANT same-process since erwin is our host. Use 0 flag instead.
    if (evHook) UnhookWinEvent(evHook);
    evHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
        nullptr, WizardWinEventCb,
        GetCurrentProcessId(), 0,
        WINEVENT_OUTOFCONTEXT);
    LogLine("[OPEN-WIZ] WinEvent hook = %p", (void*)evHook);

    // Bring erwin to foreground so keyboard input is delivered there.
    SetForegroundWindow(mainHwnd);
    Sleep(80);

    // Simulate Ctrl+Alt+T via SendInput.
    INPUT inputs[6] = {};
    inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_CONTROL;
    inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = VK_MENU;      // Alt
    inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = 'T';
    inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = 'T';          inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
    inputs[4].type = INPUT_KEYBOARD; inputs[4].ki.wVk = VK_MENU;      inputs[4].ki.dwFlags = KEYEVENTF_KEYUP;
    inputs[5].type = INPUT_KEYBOARD; inputs[5].ki.wVk = VK_CONTROL;   inputs[5].ki.dwFlags = KEYEVENTF_KEYUP;
    UINT sent = SendInput(6, inputs, sizeof(INPUT));
    LogLine("[OPEN-WIZ] SendInput sent=%u/6 (Ctrl+Alt+T)", sent);

    // IMPORTANT: this function MUST be called from a background thread (e.g.
    // Task.Run on the managed side). If called on erwin's UI thread, our
    // Sleep loops block erwin's own message pump and it can't dispatch the
    // keystroke through MFC's TranslateAccelerator. On a bg thread, we just
    // sleep; erwin's UI thread runs normally and processes the keystroke.

    // Phase 1: wait up to 15 seconds for the FEWCtor hook to fire. Ctor fires
    // on erwin's UI thread, we poll the flag from our bg thread.
    bool ctorFired = false;
    DWORD start = GetTickCount();
    while (GetTickCount() - start < 15000) {
        if (InterlockedCompareExchange(&g_autoOpenCtorFired, 0, 0) != 0) {
            ctorFired = true;
            LogLine("[OPEN-WIZ] FEWCtor fired after %lu ms", GetTickCount() - start);
            break;
        }
        Sleep(50);
    }
    if (!ctorFired) {
        LogLine("[OPEN-WIZ] timeout - FEWCtor did not fire within 15s; wizard never opened");
        UnhookWinEvent(evHook);
        return nullptr;
    }

    // Phase 2: wait for either the WinEvent callback to hide the wizard, OR
    // fall back to polling + hiding ourselves. Give the event hook 5s.
    start = GetTickCount();
    while (GetTickCount() - start < 5000) {
        HWND h = (HWND)InterlockedCompareExchange64(&g_hiddenWizardHwnd, 0, 0);
        if (h) {
            LogLine("[OPEN-WIZ] wizard hidden by WinEvent hook, hwnd=%p", h);
            UnhookWinEvent(evHook);
            return (void*)h;
        }
        // Fallback polling in case WinEvent missed it.
        auto nowList = EnumerateVisibleDialogs();
        for (HWND x : nowList) {
            if (before.find(x) != before.end()) continue;
            if (LooksLikeAlterScriptWizard(x)) {
                HideWizardAggressive(x);
                InterlockedExchange64(&g_hiddenWizardHwnd, (LONG64)x);
                LogLine("[OPEN-WIZ] fallback-hid wizard hwnd=%p", (void*)x);
                UnhookWinEvent(evHook);
                return (void*)x;
            }
        }
        Sleep(50);
    }
    UnhookWinEvent(evHook);
    LogLine("[OPEN-WIZ] ctor fired but could not find+hide wizard within 5s");
    return nullptr;
}

// Politely close a previously-opened hidden wizard. Uses WM_COMMAND IDCANCEL
// (Cancel button) which triggers the MFC CPropertySheet::OnCancel handler
// and properly calls EndDialog to release the modal loop. Plain WM_CLOSE
// is often ignored by modal dialogs.
extern "C" __declspec(dllexport) void __cdecl CloseHiddenWizard(void* hwnd) {
    if (!hwnd) return;
    LogLine("[OPEN-WIZ] closing hwnd=%p (IDCANCEL)", hwnd);
    PostMessage((HWND)hwnd, WM_COMMAND, MAKEWPARAM(IDCANCEL, BN_CLICKED), 0);
    // Also post WM_CLOSE as a fallback in case IDCANCEL routes elsewhere.
    PostMessage((HWND)hwnd, WM_CLOSE, 0, 0);
    InterlockedExchange64(&g_hiddenWizardHwnd, 0);
    // Invalidate the cached FEWPageOptions / FEWPagePreviewEx 'this' pointers.
    // The C++ objects are destroyed when the CPropertySheet modal loop exits;
    // if we kept the stale ptrs, the next GenerateAlterDdl call would try to
    // Invoke on a dangling address (SEH 0xC0000005) and return no DDL.
    InterlockedExchange64(&g_capturedFEWPO, 0);
    InterlockedExchange64(&g_capturedFEWPreviewEx, 0);
}

extern "C" __declspec(dllexport) const char* __cdecl GenerateAlterDdlStandalone(void* clientMs) {
    if (!g_stashedFeParamValid) {
        LogLine("[STANDALONE] no stashed feParam - user must open Alter Script wizard ONCE first to seed the template");
        return nullptr;
    }
    if (!clientMs) {
        LogLine("[STANDALONE] clientMs is null");
        return nullptr;
    }

    // Resolve ctor address.
    HMODULE eou = GetModuleHandleW(L"EM_EOU.dll");
    if (!eou) { LogLine("[STANDALONE] EM_EOU not loaded"); return nullptr; }
    FEWCtorFn ctor = (FEWCtorFn)GetProcAddress(eou, kFEWPageOptionsCtorSym);
    if (!ctor) { LogLine("[STANDALONE] ctor symbol missing"); return nullptr; }

    if (!g_directInvokePreview) { LogLine("[STANDALONE] Invoke not resolved"); return nullptr; }

    // Clone feParam template and OVERRIDE modelSet slot (offset 8).
    BYTE feParam[128];
    memcpy(feParam, g_stashedFeParam, sizeof(feParam));
    *(void**)(feParam + 8) = clientMs;

    // Allocate a generous heap buffer for FEWPageOptions (MFC class size
    // unknown; use 8KB to be safe). Zero-init.
    char* fewpoBuf = (char*)_aligned_malloc(8192, 16);
    if (!fewpoBuf) { LogLine("[STANDALONE] buffer alloc failed"); return nullptr; }
    memset(fewpoBuf, 0, 8192);

    // Clear prior captured DDL so we read fresh output.
    EnsureDdlLockInit();
    EnterCriticalSection(&g_ddlLock);
    if (g_lastCapturedDdl) { free(g_lastCapturedDdl); g_lastCapturedDdl = nullptr; }
    LeaveCriticalSection(&g_ddlLock);

    LogLine("[STANDALONE] constructing FEWPageOptions at %p with wsfBase=%p feParam=%p (modelSet=%p)",
        fewpoBuf, g_stashedWsfBase, (void*)feParam, clientMs);

    bool ctorOk = false;
    __try {
        ctor(fewpoBuf, g_stashedWsfBase, feParam);
        ctorOk = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[STANDALONE] ctor SEH 0x%08lX", GetExceptionCode());
    }

    if (ctorOk) {
        LogLine("[STANDALONE] ctor succeeded - calling Invoke to trigger GA");
        __try {
            InvokeFn fn = (InvokeFn)g_directInvokePreview;
            fn(fewpoBuf);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[STANDALONE] Invoke post-return SEH 0x%08lX (ignored if DDL captured)",
                GetExceptionCode());
        }
    }

    // Intentionally DO NOT call dtor on fewpoBuf - our synthetic WSFWizardBase
    // is fake and dtor may crash trying to detach from it. Heap leak per call
    // (~8KB) is acceptable for our use case; we can revisit if it matters.

    // Read captured DDL from GA detour.
    EnsureDdlLockInit();
    char* ddl = nullptr;
    EnterCriticalSection(&g_ddlLock);
    ddl = g_lastCapturedDdl;
    g_lastCapturedDdl = nullptr;
    LeaveCriticalSection(&g_ddlLock);

    if (ddl) LogLine("[STANDALONE] SUCCESS - %zu chars captured", strlen(ddl));
    else     LogLine("[STANDALONE] no DDL captured (ctor/Invoke did not trigger GA)");
    return ddl;
}

// Exported: calls InvokePreviewStringOnlyCommand on the captured FEWPageOptions
// and returns the DDL string as a malloc'd UTF-8 buffer. Caller frees via
// FreeDdlBuffer. Returns null if no wizard was previously opened or the call
// fails.

extern "C" __declspec(dllexport) const char* __cdecl CallInvokePreviewOnCaptured(void) {
    // Use FEWPageOptions captured in its ctor. Empirically proven to produce
    // the correct alter-script DDL (e.g. 331 chars for one ADD COLUMN change),
    // whereas FEWPagePreviewEx yields the full schema (154216 chars, wrong
    // for alter-script use case).
    void* self = (void*)InterlockedCompareExchange64(&g_capturedFEWPO, 0, 0);
    if (!self) { LogLine("[IPS-CALL] no captured FEWPageOptions"); return nullptr; }
    if (!g_directInvokePreview) { LogLine("[IPS-CALL] Invoke address not resolved"); return nullptr; }

    // Clear any stale DDL from prior runs so we pick up only THIS run's output.
    EnsureDdlLockInit();
    EnterCriticalSection(&g_ddlLock);
    if (g_lastCapturedDdl) { free(g_lastCapturedDdl); g_lastCapturedDdl = nullptr; }
    LeaveCriticalSection(&g_ddlLock);

    LogLine("[IPS-CALL] triggering Invoke on FEWPageOptions=%p (DDL via GA-detour)", self);
    __try {
        // Call with RCX=self. We ignore the return value (ABI is non-trivial
        // for CString — MSVC may use RAX or hidden ptr; both gave AV/SEH when
        // we tried to read them). What matters is the SIDE EFFECT: Invoke
        // runs, internally calls FEProcessor::GenerateAlterScript, our GA
        // detour captures the DDL into g_lastCapturedDdl.
        InvokeFn fn = (InvokeFn)g_directInvokePreview;
        fn(self);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Expected: SEH may fire on the return-value path. Doesn't matter.
        LogLine("[IPS-CALL] post-return SEH 0x%08lX (ignored - DDL already captured)",
            GetExceptionCode());
    }

    // Read and consume whatever the GA detour captured.
    EnsureDdlLockInit();
    char* ddl = nullptr;
    EnterCriticalSection(&g_ddlLock);
    ddl = g_lastCapturedDdl;
    g_lastCapturedDdl = nullptr;
    LeaveCriticalSection(&g_ddlLock);

    if (ddl) LogLine("[IPS-CALL] SUCCESS - %zu chars of DDL via GA detour", strlen(ddl));
    else     LogLine("[IPS-CALL] no DDL captured (GA did not fire?)");
    return ddl;
}

extern "C" __declspec(dllexport) int __cdecl InstallObserverHook(void) {
    InstallObserverHooks();
    return 0;
}

extern "C" __declspec(dllexport) const char* __cdecl GenerateAlterDdlForActiveModel(void* clientMs) {
    return (const char*)RunSilentAlterDdl(clientMs);
}

extern "C" __declspec(dllexport) const char* __cdecl GenerateAlterDdlFromCaptured(void) {
    void* clientMs = (void*)InterlockedCompareExchange64(
        (volatile LONG64*)&g_lastCapturedModelSet, 0, 0);
    if (!clientMs) { LogLine("[F2] no captured modelSet; call EnsureActiveModelSetCaptured first"); return nullptr; }
    return (const char*)RunSilentAlterDdl(clientMs);
}

extern "C" __declspec(dllexport) void __cdecl FreeDdlBuffer(const char* buf) {
    if (buf) free((void*)buf);
}

// Consume the most-recent DDL captured from GenerateAlterScript. Returns a
// malloc'd UTF-8 string that the caller must release via FreeDdlBuffer, or
// nullptr if nothing has been captured yet. Consuming also clears the
// internal buffer so subsequent reads return null until the next CC run.
extern "C" __declspec(dllexport) const char* __cdecl ConsumeLastCapturedDdl(void) {
    EnsureDdlLockInit();
    char* out = nullptr;
    EnterCriticalSection(&g_ddlLock);
    out = g_lastCapturedDdl;
    g_lastCapturedDdl = nullptr;
    LeaveCriticalSection(&g_ddlLock);
    return out;
}

// Clear the in-memory buffer without consuming it (used before triggering a
// new CC run to ensure we read a fresh capture, not a stale one).
extern "C" __declspec(dllexport) void __cdecl ClearCapturedDdl(void) {
    EnsureDdlLockInit();
    EnterCriticalSection(&g_ddlLock);
    if (g_lastCapturedDdl) { free(g_lastCapturedDdl); g_lastCapturedDdl = nullptr; }
    LeaveCriticalSection(&g_ddlLock);
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
        TruncateLog();   // fresh log per erwin process
        LogLine("DllMain DLL_PROCESS_ATTACH in PID %lu", GetCurrentProcessId());
    } else if (reason == DLL_PROCESS_DETACH) {
        LogLine("DllMain DLL_PROCESS_DETACH");
    }
    return TRUE;
}
