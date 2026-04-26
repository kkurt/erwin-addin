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
        // Two-byte opcodes starting with 0x0F.
        case 0x0F: {
            BYTE b1 = p[1];
            switch (b1) {
                // MOVZX r32/64, r/m8   (0F B6 /r)
                // MOVZX r32/64, r/m16  (0F B7 /r)
                // MOVSX r32/64, r/m8   (0F BE /r)
                // MOVSX r32/64, r/m16  (0F BF /r)
                case 0xB6: case 0xB7: case 0xBE: case 0xBF: {
                    BYTE modrm = p[2];
                    BYTE mod = modrm >> 6;
                    BYTE rm  = modrm & 7;
                    size_t sz = 3; // 0F + opcode + modrm
                    if (mod != 3 && rm == 4) sz += 1;
                    if (mod == 1) sz += 1;
                    else if (mod == 2) sz += 4;
                    else if (mod == 0 && rm == 5) return 0;  // RIP-relative
                    return sz;
                }
                default:
                    return 0;
            }
        }
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

    // Compute the instruction-aligned copy size first (smallest prefix >= 14
    // bytes). We only need the copied bytes to be free of relative branches;
    // anything past copyLen stays at the original site and is reached via the
    // trampoline's terminating jmp. Scanning too far (e.g. the full 20 bytes
    // of prologue dump) used to false-reject safe detours where a `call
    // rel32` lives AFTER the copy boundary.
    size_t copyLen = AlignedCopyLen(t, 14);
    if (copyLen == 0) {
        LogLine("[HOOK] ABORT: could not decode prologue to a 14-byte boundary.");
        return false;
    }
    if (!PrologueSafe(t, copyLen)) {
        LogLine("[HOOK] ABORT: prologue contains relative jump/call within copy window (copyLen=%zu).", copyLen);
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
static void ResolveCCInspectionSymbols(void);
extern "C" __declspec(dllexport) int __cdecl CCInsp_StartPoller(void);
extern "C" __declspec(dllexport) int __cdecl CCInsp_InstallOnFeHook(void);
extern "C" __declspec(dllexport) int __cdecl CCInsp_InstallEdrHooks(void);
static void InstallEccPipelineHooks(void);

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

    // ----- D1-spike: resolve CC inspection symbols (no hooks, just addresses)
    ResolveCCInspectionSymbols();

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

// =====================================================================
// F2-PAIR: variant of RunSilentAlterDdl that takes BOTH ModelSets and
// SKIPS PrepareServerModelSet. Used by the dirty-aware add-in flow:
// the caller already created a session-less duplicate PU via
// app.PersistenceUnits.Create(... ;Duplicate=YES, modelLongId) and
// passes (dupModelSet, activeModelSet). The active PU's in-memory
// dirty buffer becomes the clientMs, and the duplicate's clean
// Mart-fetched ModelSet becomes the serverMs.
// =====================================================================
static char* RunSilentAlterDdlWithServerMs(void* serverMs, void* clientMs) {
    LogLine("===== [F2-PAIR] RunSilentAlterDdlWithServerMs(serverMs=%p, clientMs=%p) =====",
        serverMs, clientMs);
    if (!serverMs || !clientMs) { LogLine("[F2-PAIR] null arg"); return nullptr; }
    if (!ResolveFaz2Symbols()) { LogLine("[F2-PAIR] symbol resolution failed"); return nullptr; }

    void* as1 = nullptr;
    void* as2 = nullptr;
    char* ccBuf = (char*)_aligned_malloc(4096, 16);
    char* fepBuf = (char*)_aligned_malloc(4096, 16);
    char* result = nullptr;
    bool ccConstructed = false;
    bool fepConstructed = false;

    if (!ccBuf || !fepBuf) { LogLine("[F2-PAIR] buffer alloc failed"); goto cleanup; }
    memset(ccBuf, 0, 4096);
    memset(fepBuf, 0, 4096);

    if (g_activateSilent) {
        __try { g_activateSilent(1); LogLine("[F2-PAIR] silent mode ON"); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2-PAIR] ActivateSilentMode(1) SEH"); }
    }

    __try {
        // Step 1: SKIP PrepareServerModelSet - caller supplied serverMs.
        // PrepareServerModelSet normally returns as1 alongside the serverMs;
        // since we already have serverMs, we'd need to manufacture as1.
        // First attempt: InitClientAS on serverMs (it has the same shape as
        // any other MS in memory). If that AVs (it does, when serverMs is a
        // session-less duplicate), fall back to using as2 for both slots
        // (let MCX::ctor see them as identical pre-state holders) and
        // ultimately to a null - some MCX paths only consult as2.
        LogLine("[F2-PAIR] step 1: InitializeClientActionSummary(clientMs) -> as2...");
        bool ok2 = g_initClientAs(clientMs, &as2);
        LogLine("[F2-PAIR]   rc=%d as2=%p", (int)ok2, as2);
        if (!ok2 || !as2) { LogLine("[F2-PAIR] InitClientAs(clientMs) failed"); goto finally_block; }

        LogLine("[F2-PAIR] step 2: probe InitializeClientActionSummary(serverMs) under SEH...");
        __try { ok2 = g_initClientAs(serverMs, &as1); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[F2-PAIR]   InitClientAs(serverMs) AV 0x%08lX (expected for session-less dup)", GetExceptionCode());
            as1 = nullptr;
        }
        LogLine("[F2-PAIR]   as1=%p", as1);
        // Fall back: reuse as2 for both. If that also blows up downstream
        // we'll try null next iteration of the probe.
        if (!as1)
        {
            LogLine("[F2-PAIR]   reusing as2 as as1 (server-side AS fallback)");
            as1 = as2;
        }

        LogLine("[F2-PAIR] step 3: MCXInvokeCompleteCompare ctor(serverMs, clientMs, as1, as2)...");
        g_mcxCtor(ccBuf, serverMs, clientMs, as1, as2);
        ccConstructed = true;

        LogLine("[F2-PAIR] step 4: MCXInvokeCompleteCompare::Execute(clientMs)...");
        bool ccOk = g_mcxExecute(ccBuf, clientMs);
        LogLine("[F2-PAIR]   Execute -> %d", (int)ccOk);
        if (!ccOk) { LogLine("[F2-PAIR] Execute returned false"); goto finally_block; }

        LogLine("[F2-PAIR] step 5: FEProcessor ctor + GenerateAlterScript...");
        g_fepCtor(fepBuf);
        fepConstructed = true;
        int feRv = g_origGenerateAlter
            ? g_origGenerateAlter(fepBuf, clientMs, as2, nullptr, false)
            : -1;
        LogLine("[F2-PAIR]   GenerateAlterScript rv=%d (0=OK)", feRv);
        if (feRv != 0) goto finally_block;

        LogLine("[F2-PAIR] step 6: GetScript + concatenate...");
        void* vec = g_getScript(fepBuf);
        LogLine("[F2-PAIR]   vec=%p", vec);
        result = ConcatScriptVector(vec);
        LogLine("[F2-PAIR]   result=%p (len=%zu)", (void*)result, result ? strlen(result) : 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[F2-PAIR] SEH 0x%08lX during pipeline", GetExceptionCode());
    }

finally_block:
    if (fepConstructed) { __try { g_fepDtor(fepBuf); } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2-PAIR] fepDtor SEH"); } }
    if (ccConstructed)  { __try { g_mcxDtor(ccBuf);  } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2-PAIR] mcxDtor SEH"); } }

cleanup:
    if (fepBuf) _aligned_free(fepBuf);
    if (ccBuf)  _aligned_free(ccBuf);
    if (g_activateSilent) {
        __try { g_activateSilent(0); LogLine("[F2-PAIR] silent mode OFF"); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[F2-PAIR] ActivateSilentMode(0) SEH"); }
    }
    LogLine("===== [F2-PAIR] done, result=%s =====", result ? "(text)" : "(null)");
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

// Emit top-N caller frames (module+RVA) - for pinpointing AS builder.
// Increased from 10 to 30 frames so dispatcher/WndProc call chains are
// visible above the low-level GDM/EDR frames.
static void LogStack(const char* tag) {
    void* frames[30];
    USHORT captured = CaptureStackBackTrace(0, 30, frames, nullptr);
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

// F2-PAIR export. Caller supplies BOTH ModelSet pointers - typically the
// active dirty PU's ModelSet (clientMs) and a Mart-Create()'d duplicate
// PU's ModelSet (serverMs). Skips PrepareServerModelSet entirely - the
// duplicate already gives us the clean Mart-fetched server side, so the
// CC pipeline can run end-to-end without a Mart-version-history fetch.
extern "C" __declspec(dllexport) const char* __cdecl GenerateAlterDdlWithServerMs(
    void* serverMs, void* clientMs)
{
    return (const char*)RunSilentAlterDdlWithServerMs(serverMs, clientMs);
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

// ---------------------------------------------------------------------------
// Cleanup-time WinEvent hook: hide ANY new dialog (#32770 / Afx wizard frame)
// in erwin's process the moment it is created, BEFORE its first paint. The
// MartMartAutomation cleanup path triggers a cascade of dialogs (Mart Offline,
// Save As pickers, Close Model checklist, "Save changes?" prompts) when the
// CC wizard closes after Apply-to-Right; without this hook each one paints
// briefly even though we dismiss them programmatically. With this hook
// installed for the cleanup window, the dialogs never reach first-paint.
//
// Differs from WizardWinEventCb in two ways:
//   - matches *any* dialog/wizard class, not just "Alter Script" by title
//   - does not stop after first hit (cleanup spawns multiple dialogs)
// ---------------------------------------------------------------------------

static volatile LONG g_cleanupHookActive = 0;
static HWINEVENTHOOK g_cleanupEvHook = nullptr;
static int g_cleanupHidCount = 0;

static bool LooksLikeCleanupTarget(HWND hwnd) {
    char cls[64] = {0};
    GetClassNameA(hwnd, cls, sizeof(cls));
    if (strcmp(cls, "#32770") == 0) return true;          // dialog frame
    if (strncmp(cls, "Afx", 3) == 0) return true;          // MFC frame (CC wizard variants)
    return false;
}

static void CALLBACK CleanupWinEventCb(
    HWINEVENTHOOK /*hook*/, DWORD event, HWND hwnd,
    LONG idObject, LONG /*idChild*/, DWORD /*eventThread*/, DWORD /*eventTime*/)
{
    if (!hwnd) return;
    if (idObject != OBJID_WINDOW) return;
    if (InterlockedCompareExchange(&g_cleanupHookActive, 0, 0) == 0) return;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) return;
    if (!LooksLikeCleanupTarget(hwnd)) return;

    // Skip windows that are already hidden (e.g. the original CC wizard we
    // hid on session start - HideWizardAggressive already moved it off-screen).
    LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((ex & WS_EX_LAYERED) != 0) {
        BYTE alpha = 255;
        DWORD flags = 0;
        if (GetLayeredWindowAttributes(hwnd, nullptr, &alpha, &flags) && alpha == 0) {
            return;
        }
    }

    HideWizardAggressive(hwnd);
    g_cleanupHidCount++;
    char title[128] = {0};
    GetWindowTextA(hwnd, title, sizeof(title));
    char cls[64] = {0};
    GetClassNameA(hwnd, cls, sizeof(cls));
    LogLine("[CLEAN-EVT] hid hwnd=%p cls='%s' title='%s' event=%lu",
        hwnd, cls, title, event);
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_CleanupHookInstall(void) {
    if (g_cleanupEvHook) {
        LogLine("[CLEAN-EVT] install: hook already installed (%p)", g_cleanupEvHook);
        return -1;
    }
    g_cleanupHidCount = 0;
    InterlockedExchange(&g_cleanupHookActive, 1);
    g_cleanupEvHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
        nullptr, CleanupWinEventCb,
        GetCurrentProcessId(), 0,
        WINEVENT_OUTOFCONTEXT);
    LogLine("[CLEAN-EVT] install: hook=%p", g_cleanupEvHook);
    return g_cleanupEvHook ? 0 : -2;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_CleanupHookUninstall(void) {
    InterlockedExchange(&g_cleanupHookActive, 0);
    int hid = g_cleanupHidCount;
    if (g_cleanupEvHook) {
        UnhookWinEvent(g_cleanupEvHook);
        g_cleanupEvHook = nullptr;
        LogLine("[CLEAN-EVT] uninstall: hid %d dialog(s) during cleanup", hid);
    }
    return hid;
}

extern "C" __declspec(dllexport) int __cdecl UninstallHook(void) {
    // For the spike we intentionally leave the detour installed for the process
    // lifetime. Restoring the original bytes mid-flight risks a race if erwin
    // is currently in GenerateAlterScript. A rebuild/inject cycle is cheap.
    LogLine("UninstallHook: no-op (detour intentionally left armed).");
    return 0;
}

// ---------------------------------------------------------------------------
// D1-spike: CC inspection exports
// Purpose: observe what CERwinFEData and ELC2 globals contain during a manual
// Complete Compare flow, to determine whether we can programmatically drive
// ApplyCCSilentMode + read the resulting ActionSummary + feed it to the
// GenerateAlterScript path we already intercept.
// ---------------------------------------------------------------------------

// CERwinFEData static accessors (EM_EOU). No args, return pointer.
typedef void* (__cdecl* FEData_GetASFn)(void);
typedef void* (__cdecl* FEData_GetMsFn)(void);
typedef void  (__cdecl* FEData_SetASFn)(void* as);
typedef void  (__cdecl* FEData_SetMsFn)(void* ms);
typedef void  (__cdecl* FEData_ClearFn)(void);

static FEData_GetASFn     g_feDataGetAs    = nullptr;
static FEData_GetMsFn     g_feDataGetMs    = nullptr;
static FEData_SetASFn     g_feDataSetAs    = nullptr;
static FEData_SetMsFn     g_feDataSetMs    = nullptr;
static FEData_ClearFn     g_feDataClear    = nullptr;

// EM_ELC2 data export — pointer variable's address (dereference to read).
static void** g_elc2GblAsPtrAddr = nullptr;

// EM_EOU CERwinFEData static member addresses (data exports). Reading these
// directly is safer than calling the getters from a background thread.
static void** g_eou_m_xAs        = nullptr;   // m_xActionSummary
static void** g_eou_m_modelSet   = nullptr;   // m_modelSet
static void** g_eou_m_pxItem     = nullptr;   // m_pxItem (AS item)

// EM_ECC CWizInterface::ApplyCCSilentMode — 4-arg variant.
//   int ApplyCCSilentMode(GDMModelSetI* left, GDMModelSetI* right,
//                         CCCompareLevelType_e level, CString outFile);
// CString on x64 is a single 8-byte pointer to internal heap data.
typedef int (__cdecl* ApplyCCSilentFn)(void* left, void* right, int level, void* cstrOut);
static ApplyCCSilentFn g_applyCCSilent = nullptr;

static const char* kCERwinFED_GetAsSym      = "?GetActionSummary@CERwinFEData@@SAPEAVGDMActionSummary@@XZ";
static const char* kCERwinFED_GetMsSym      = "?GetModelSet@CERwinFEData@@SAPEAVGDMModelSetI@@XZ";
static const char* kCERwinFED_SetAsSym      = "?SetActionSummary@CERwinFEData@@SAXPEAVGDMActionSummary@@@Z";
static const char* kCERwinFED_SetMsSym      = "?SetModelSet@CERwinFEData@@SAXPEAVGDMModelSetI@@@Z";
static const char* kCERwinFED_ClearSym      = "?ClearERwinFEData@CERwinFEData@@SAXXZ";
static const char* kELC2_GblAsSym           = "?gbl_pxActionSummary@@3PEAVGDMActionSummary@@EA";
static const char* kApplyCCSilent4Sym       = "?ApplyCCSilentMode@CWizInterface@@SAHPEAVGDMModelSetI@@0W4CCCompareLevelType_e@@V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@@Z";
static const char* kEOU_m_xAsSym            = "?m_xActionSummary@CERwinFEData@@0PEAVGDMActionSummary@@EA";
static const char* kEOU_m_modelSetSym       = "?m_modelSet@CERwinFEData@@0PEAVGDMModelSetI@@EA";
static const char* kEOU_m_pxItemSym         = "?m_pxItem@CERwinFEData@@0PEAVGDMActionSummaryItem@@EA";

static void ResolveCCInspectionSymbols(void) {
    HMODULE eou  = GetModuleHandleW(L"EM_EOU.dll");
    HMODULE elc2 = GetModuleHandleW(L"EM_ELC2.DLL");
    if (!elc2) elc2 = GetModuleHandleW(L"EM_ELC2.dll");
    HMODULE ecc  = GetModuleHandleW(L"EM_ECC.dll");

    if (eou) {
        g_feDataGetAs = (FEData_GetASFn)GetProcAddress(eou, kCERwinFED_GetAsSym);
        g_feDataGetMs = (FEData_GetMsFn)GetProcAddress(eou, kCERwinFED_GetMsSym);
        g_feDataSetAs = (FEData_SetASFn)GetProcAddress(eou, kCERwinFED_SetAsSym);
        g_feDataSetMs = (FEData_SetMsFn)GetProcAddress(eou, kCERwinFED_SetMsSym);
        g_feDataClear = (FEData_ClearFn)GetProcAddress(eou, kCERwinFED_ClearSym);
        LogLine("[CC-INSP] EOU getAs=%p getMs=%p setAs=%p setMs=%p clear=%p",
            (void*)g_feDataGetAs, (void*)g_feDataGetMs,
            (void*)g_feDataSetAs, (void*)g_feDataSetMs, (void*)g_feDataClear);
    } else {
        LogLine("[CC-INSP] EM_EOU.dll not loaded");
    }
    if (elc2) {
        g_elc2GblAsPtrAddr = (void**)GetProcAddress(elc2, kELC2_GblAsSym);
        LogLine("[CC-INSP] ELC2 gbl_pxActionSummary addr=%p", (void*)g_elc2GblAsPtrAddr);
    } else {
        LogLine("[CC-INSP] EM_ELC2.DLL not loaded");
    }
    if (eou) {
        g_eou_m_xAs      = (void**)GetProcAddress(eou, kEOU_m_xAsSym);
        g_eou_m_modelSet = (void**)GetProcAddress(eou, kEOU_m_modelSetSym);
        g_eou_m_pxItem   = (void**)GetProcAddress(eou, kEOU_m_pxItemSym);
        LogLine("[CC-INSP] EOU data addrs: m_xAs=%p m_modelSet=%p m_pxItem=%p",
            (void*)g_eou_m_xAs, (void*)g_eou_m_modelSet, (void*)g_eou_m_pxItem);
    }
    if (ecc) {
        g_applyCCSilent = (ApplyCCSilentFn)GetProcAddress(ecc, kApplyCCSilent4Sym);
        LogLine("[CC-INSP] ECC ApplyCCSilentMode(4) addr=%p", (void*)g_applyCCSilent);
    } else {
        LogLine("[CC-INSP] EM_ECC.dll not loaded");
    }

    // Auto-start the CC-state poller so user doesn't need to click anything.
    // It runs for the entire erwin session and emits one log line per change.
    CCInsp_StartPoller();
    LogLine("[CC-INSP] CC-state poller started (100ms interval)");

    // Auto-install the ELA::OnFE detour so we can log every alter-script
    // entry (whether programmatic or via the 'Right Alter Script' button).
    int rcHook = CCInsp_InstallOnFeHook();
    LogLine("[CC-INSP] OnFE hook install rc=%d", rcHook);

    // Auto-install EDR transaction-tracker hooks so we can capture the
    // RIGHT-side modelSet during CC + Apply-to-Right without requiring
    // the user to click 'Right Alter Script' manually first.
    int rcEdr = CCInsp_InstallEdrHooks();
    LogLine("[CC-INSP] EDR hooks install rc=%d", rcEdr);

    // Auto-install CC pipeline hooks in EM_ECC to observe which CWizInterface
    // entry point the Complete Compare + Apply-to-Right flow actually uses.
    InstallEccPipelineHooks();
}

// Exported: returns CERwinFEData::GetActionSummary() or null.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetFEDataActionSummary(void) {
    if (!g_feDataGetAs) return nullptr;
    __try { return g_feDataGetAs(); }
    __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-INSP] GetActionSummary SEH"); return nullptr; }
}

// Exported: returns CERwinFEData::GetModelSet() or null.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetFEDataModelSet(void) {
    if (!g_feDataGetMs) return nullptr;
    __try { return g_feDataGetMs(); }
    __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-INSP] GetModelSet SEH"); return nullptr; }
}

// Exported: dereferences ELC2's gbl_pxActionSummary and returns the pointer.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetELC2GlobalAs(void) {
    if (!g_elc2GblAsPtrAddr) return nullptr;
    __try { return *g_elc2GblAsPtrAddr; }
    __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-INSP] read gbl_pxAs SEH"); return nullptr; }
}

// ---------------------------------------------------------------------------
// gbl_pxActionSummary write-watch via guard page + vectored exception handler.
// Purpose: catch the FIRST write to gbl_pxAs and log the writer's RIP + stack
// so we can identify the function that populates it. Once caught, the guard
// is removed (one-shot) so normal execution resumes.
// ---------------------------------------------------------------------------
static volatile LONG g_asWatchActive = 0;
static PVOID g_asWatchHandle = nullptr;
static DWORD g_asWatchOldProt = 0;

static LONG CALLBACK AsWriteWatchVEH(PEXCEPTION_POINTERS ep) {
    if (!ep || !ep->ExceptionRecord) return EXCEPTION_CONTINUE_SEARCH;
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;
    // ExceptionInformation[0]=1 means write, [1]=faulting address.
    if (ep->ExceptionRecord->NumberParameters < 2) return EXCEPTION_CONTINUE_SEARCH;
    ULONG_PTR isWrite = ep->ExceptionRecord->ExceptionInformation[0];
    ULONG_PTR faultAddr = ep->ExceptionRecord->ExceptionInformation[1];
    if (!g_elc2GblAsPtrAddr) return EXCEPTION_CONTINUE_SEARCH;

    // Is the fault anywhere on our guarded page? If not, it's someone else's AV.
    ULONG_PTR pageMask = ~((ULONG_PTR)0xFFF);
    if ((faultAddr & pageMask) != ((ULONG_PTR)g_elc2GblAsPtrAddr & pageMask))
        return EXCEPTION_CONTINUE_SEARCH;

    bool isOurTarget = (faultAddr == (ULONG_PTR)g_elc2GblAsPtrAddr);
    void* rip = (void*)ep->ContextRecord->Rip;
    if (!isOurTarget) {
        LogLine("[AS-WATCH] collateral AV on watched page: faultAddr=%p (not gbl_pxAs) - disarming",
            (void*)faultAddr);
        // Fall through to disarm & continue; we'll miss the real write but avoid deadlock.
    }
    if (isOurTarget) {
        LogLine("[AS-WATCH] HIT isWrite=%lu faultAddr=%p RIP=%p RAX=%p RCX=%p RDX=%p R8=%p R9=%p",
            (unsigned long)isWrite, (void*)faultAddr, rip,
            (void*)ep->ContextRecord->Rax, (void*)ep->ContextRecord->Rcx,
            (void*)ep->ContextRecord->Rdx, (void*)ep->ContextRecord->R8,
            (void*)ep->ContextRecord->R9);
        // Dump a short stack using the ContextRecord's RBP chain. x64 compilers
        // may omit RBP frames in optimized code - best-effort only.
        ULONG_PTR* rbp = (ULONG_PTR*)ep->ContextRecord->Rbp;
        for (int i = 0; i < 10 && rbp; ++i) {
            __try {
                ULONG_PTR retAddr = rbp[1];
                LogLine("[AS-WATCH-STK] #%d  retAddr=%p  rbp=%p", i, (void*)retAddr, (void*)rbp);
                rbp = (ULONG_PTR*)rbp[0];
                if (!rbp) break;
            } __except (EXCEPTION_EXECUTE_HANDLER) { break; }
        }
        // Also dump RSP-based return addresses (more reliable for x64 /Oy).
        ULONG_PTR* rsp = (ULONG_PTR*)ep->ContextRecord->Rsp;
        for (int i = 0; i < 12; ++i) {
            __try {
                LogLine("[AS-WATCH-RSP] +0x%02X  val=%p", i*8, (void*)rsp[i]);
            } __except (EXCEPTION_EXECUTE_HANDLER) { break; }
        }
    }

    // Remove protection so the write succeeds and we don't re-fire forever.
    DWORD prevProt = 0;
    VirtualProtect(g_elc2GblAsPtrAddr, sizeof(void*), PAGE_READWRITE, &prevProt);
    InterlockedExchange(&g_asWatchActive, 0);
    if (g_asWatchHandle) {
        RemoveVectoredExceptionHandler(g_asWatchHandle);
        g_asWatchHandle = nullptr;
    }
    LogLine("[AS-WATCH] protection removed, VEH uninstalled - resuming write");
    return EXCEPTION_CONTINUE_EXECUTION;
}

// Exported: arm the one-shot write-watch on gbl_pxActionSummary. Call this
// right before you trigger the action that should populate the global.
extern "C" __declspec(dllexport) int __cdecl CCInsp_ArmAsWriteWatch(void) {
    if (!g_elc2GblAsPtrAddr) { LogLine("[AS-WATCH] gbl_pxAs addr not resolved"); return -1; }
    if (InterlockedCompareExchange(&g_asWatchActive, 1, 0) != 0) {
        LogLine("[AS-WATCH] already armed");
        return 1;
    }
    g_asWatchHandle = AddVectoredExceptionHandler(1 /*first*/, AsWriteWatchVEH);
    if (!g_asWatchHandle) { LogLine("[AS-WATCH] AddVectoredExceptionHandler failed"); InterlockedExchange(&g_asWatchActive, 0); return -2; }
    DWORD prev = 0;
    if (!VirtualProtect(g_elc2GblAsPtrAddr, sizeof(void*), PAGE_READONLY, &prev)) {
        LogLine("[AS-WATCH] VirtualProtect failed err=0x%lX", GetLastError());
        RemoveVectoredExceptionHandler(g_asWatchHandle);
        g_asWatchHandle = nullptr;
        InterlockedExchange(&g_asWatchActive, 0);
        return -3;
    }
    g_asWatchOldProt = prev;
    LogLine("[AS-WATCH] armed - gbl_pxAs @ %p now PAGE_READONLY (was 0x%lX)",
        (void*)g_elc2GblAsPtrAddr, prev);
    return 0;
}

// ---------------------------------------------------------------------------
// CC-state polling thread.
// Every 100ms reads the 4 pointer-sized globals we care about, and emits a log
// line whenever any of them changes. Cheap: no calls into erwin code, just
// naked memory loads from already-exported data addresses.
// ---------------------------------------------------------------------------
static volatile LONG g_ccPollerStop = 0;
static HANDLE g_ccPollerThread = nullptr;

static DWORD WINAPI CCStatePollerProc(LPVOID) {
    LogLine("[CC-POLL] thread started");
    void* last_elc2_as = nullptr;
    void* last_fed_as  = nullptr;
    void* last_fed_ms  = nullptr;
    void* last_fed_item = nullptr;
    void* last_cap_ms  = nullptr;

    // Prime: read once without logging so we only log *changes* after start.
    __try {
        if (g_elc2GblAsPtrAddr) last_elc2_as = *g_elc2GblAsPtrAddr;
        if (g_eou_m_xAs)        last_fed_as  = *g_eou_m_xAs;
        if (g_eou_m_modelSet)   last_fed_ms  = *g_eou_m_modelSet;
        if (g_eou_m_pxItem)     last_fed_item = *g_eou_m_pxItem;
        last_cap_ms = (void*)InterlockedCompareExchange64(&g_lastCapturedModelSet, 0, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    while (InterlockedCompareExchange(&g_ccPollerStop, 0, 0) == 0) {
        Sleep(100);
        void* cur_elc2_as = nullptr;
        void* cur_fed_as  = nullptr;
        void* cur_fed_ms  = nullptr;
        void* cur_fed_item = nullptr;
        void* cur_cap_ms  = nullptr;
        __try {
            if (g_elc2GblAsPtrAddr) cur_elc2_as = *g_elc2GblAsPtrAddr;
            if (g_eou_m_xAs)        cur_fed_as  = *g_eou_m_xAs;
            if (g_eou_m_modelSet)   cur_fed_ms  = *g_eou_m_modelSet;
            if (g_eou_m_pxItem)     cur_fed_item = *g_eou_m_pxItem;
            cur_cap_ms = (void*)InterlockedCompareExchange64(&g_lastCapturedModelSet, 0, 0);
        } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }

        if (cur_elc2_as != last_elc2_as) {
            LogLine("[CC-POLL] ELC2.gbl_pxActionSummary CHANGED %p -> %p", last_elc2_as, cur_elc2_as);
            last_elc2_as = cur_elc2_as;
        }
        if (cur_fed_as != last_fed_as) {
            LogLine("[CC-POLL] CERwinFEData.m_xActionSummary CHANGED %p -> %p", last_fed_as, cur_fed_as);
            last_fed_as = cur_fed_as;
        }
        if (cur_fed_ms != last_fed_ms) {
            LogLine("[CC-POLL] CERwinFEData.m_modelSet CHANGED %p -> %p", last_fed_ms, cur_fed_ms);
            last_fed_ms = cur_fed_ms;
        }
        if (cur_fed_item != last_fed_item) {
            LogLine("[CC-POLL] CERwinFEData.m_pxItem CHANGED %p -> %p", last_fed_item, cur_fed_item);
            last_fed_item = cur_fed_item;
        }
        if (cur_cap_ms != last_cap_ms) {
            LogLine("[CC-POLL] g_lastCapturedModelSet CHANGED %p -> %p", last_cap_ms, cur_cap_ms);
            last_cap_ms = cur_cap_ms;
        }
    }
    LogLine("[CC-POLL] thread stopping");
    return 0;
}

// Exported: start/stop the CC state poller. Idempotent.
extern "C" __declspec(dllexport) int __cdecl CCInsp_StartPoller(void) {
    if (g_ccPollerThread) return 0;   // already running
    InterlockedExchange(&g_ccPollerStop, 0);
    g_ccPollerThread = CreateThread(nullptr, 0, CCStatePollerProc, nullptr, 0, nullptr);
    return g_ccPollerThread ? 0 : -1;
}

extern "C" __declspec(dllexport) void __cdecl CCInsp_StopPoller(void) {
    InterlockedExchange(&g_ccPollerStop, 1);
    if (g_ccPollerThread) {
        WaitForSingleObject(g_ccPollerThread, 2000);
        CloseHandle(g_ccPollerThread);
        g_ccPollerThread = nullptr;
    }
}

// ---------------------------------------------------------------------------
// EDRAlterNameCaching::GetTrasactionSummary  (note typo: "Trasaction")
// Signature: static GDMActionSummary* GetTrasactionSummary(uint flags, GDMModelSetI* ms)
// This is the function ELA::OnFE (the Right Alter Script handler) calls to
// obtain the ActionSummary that then gets written to gbl_pxActionSummary.
// Captured via write-watch at EM_ELA+0xB2256.
// ---------------------------------------------------------------------------
typedef void* (__cdecl* GetTrasactionSummaryFn)(unsigned int flags, void* ms);
static GetTrasactionSummaryFn g_getTrasactionSummary = nullptr;

static const char* kEdrGetTrasactionSummarySym =
    "?GetTrasactionSummary@EDRAlterNameCaching@@SAPEAVGDMActionSummary@@IPEAVGDMModelSetI@@@Z";

// Exported: call EDRAlterNameCaching::GetTrasactionSummary(flags, ms). The
// returned pointer is the AS reflecting transactions recorded on `ms` since
// the last Register() / RegisterStartTransactionId() call. After the user
// has done a manual CC + Apply-to-Right, this should return the Mart-Mart
// alter AS.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetTrasactionSummary(
    unsigned int flags, void* ms)
{
    if (!g_getTrasactionSummary) {
        HMODULE edr = GetModuleHandleW(L"EM_EDR.dll");
        if (!edr) edr = LoadLibraryW(L"EM_EDR.dll");
        if (edr) {
            g_getTrasactionSummary = (GetTrasactionSummaryFn)GetProcAddress(edr, kEdrGetTrasactionSummarySym);
            LogLine("[CC-INSP] EDR GetTrasactionSummary addr=%p", (void*)g_getTrasactionSummary);
        }
    }
    if (!g_getTrasactionSummary) { LogLine("[CC-INSP] GetTrasactionSummary unresolved"); return nullptr; }
    if (!ms) { LogLine("[CC-INSP] GetTrasactionSummary: ms is null"); return nullptr; }
    void* rv = nullptr;
    __try { rv = g_getTrasactionSummary(flags, ms); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[CC-INSP] GetTrasactionSummary SEH 0x%08lX", GetExceptionCode());
        return nullptr;
    }
    LogLine("[CC-INSP] GetTrasactionSummary(flags=%u, ms=%p) = %p", flags, ms, rv);
    return rv;
}

// ---------------------------------------------------------------------------
// ELA::OnFE - the "Right Alter Script" button handler in Resolve Differences.
// Signature: Success_e OnFE(GDMModelSetI* ms, bool, unsigned int flags)
// Internally calls EDRAlterNameCaching::GetTrasactionSummary, writes
// gbl_pxActionSummary, and opens the Alter Script wizard (modal). Our
// FEW-CTOR hook + hide + InvokePreview chain then captures the DDL.
// ---------------------------------------------------------------------------
typedef int (__cdecl* ElaOnFeFn)(void* ms, bool flag, unsigned int flags);
static ElaOnFeFn g_elaOnFe = nullptr;
static ElaOnFeFn g_origElaOnFe = nullptr;   // trampoline after hook install
// Most-recent GDMModelSetI* passed to ELA::OnFE. Set by every call through
// our detour, including the user's manual 'Right Alter Script' click. The
// Mart-Mart orchestrator uses this pointer to pass the RIGHT-side model to
// OnFE (the side being altered), rather than the LEFT-side pointer we
// normally capture via FEModel_DDL or GA-detour on the active PU.
static volatile LONG64 g_lastOnFeMs = 0;

// ---------------------------------------------------------------------------
// EDRAlterNameCaching transaction-tracker hooks.
// When the user does Resolve Diff -> Apply-to-Right, erwin records the
// resulting transactions on the RIGHT modelSet via EDRAlterNameCaching's
// static helpers. Hooking them captures v1 (the right model) WITHOUT
// requiring the user to click 'Right Alter Script' once manually first.
// ---------------------------------------------------------------------------
typedef void (__cdecl* EdrRegisterFn)(void* ms, bool b);
typedef void (__cdecl* EdrRegisterStartFn)(void* ms, unsigned int id);
static EdrRegisterFn g_origEdrRegister = nullptr;
static EdrRegisterStartFn g_origEdrRegisterStart = nullptr;
// Last ms seen by either EDR hook (the CC/Apply-to-Right target model).
static volatile LONG64 g_lastEdrMs = 0;
// Counter of RegsiterStartTransactionId invocations. Used by managed code
// to wait for the Apply-to-Right deferred XTP cascade to finish firing
// transactions before calling OnFE (which empties the ActionSummary if
// called too early).
static volatile LONG g_edrTxCount = 0;
// Chronological list of distinct ms pointers seen by our various hooks.
// Used by the Mart-Mart spike to identify v3 (first/left) and v1 (second/right).
// Simple fixed-size; we don't expect more than a few MSs per session.
static const size_t kMaxSeenMs = 16;
static volatile LONG64 g_seenMs[kMaxSeenMs] = { 0 };
static volatile LONG g_seenMsCount = 0;
static CRITICAL_SECTION g_seenMsLock;
static LONG g_seenMsLockInit = 0;

static void TrackMsSeen(void* ms) {
    if (!ms) return;
    if (InterlockedCompareExchange(&g_seenMsLockInit, 1, 0) == 0) {
        InitializeCriticalSection(&g_seenMsLock);
    }
    EnterCriticalSection(&g_seenMsLock);
    LONG n = g_seenMsCount;
    bool found = false;
    for (LONG i = 0; i < n; ++i) {
        if ((LONG64)ms == g_seenMs[i]) { found = true; break; }
    }
    if (!found && n < (LONG)kMaxSeenMs) {
        g_seenMs[n] = (LONG64)ms;
        g_seenMsCount = n + 1;
        LogLine("[MS-TRACK] new ms[%ld] = %p", n, ms);
    }
    LeaveCriticalSection(&g_seenMsLock);
}

static const char* kEdrRegisterSym         =
    "?Register@EDRAlterNameCaching@@SAXPEAVGDMModelSetI@@_N@Z";
static const char* kEdrRegisterStartSym    =
    "?RegsiterStartTransactionId@EDRAlterNameCaching@@SAXPEAVGDMModelSetI@@I@Z";

static void __cdecl EdrRegisterHook(void* ms, bool b) {
    LogLine("[EDR-REG] Register ms=%p bool=%d", ms, (int)b);
    InterlockedExchange64(&g_lastEdrMs, (LONG64)ms);
    TrackMsSeen(ms);
    if (g_origEdrRegister) {
        __try { g_origEdrRegister(ms, b); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[EDR-REG] trampoline SEH"); }
    }
}

// When true, the EDR hook dumps its call stack every time it fires. Used to
// identify which ELC2 internal function triggers Apply-to-Right transactions.
static volatile LONG g_edrStackTrace = 0;

static void __cdecl EdrRegisterStartHook(void* ms, unsigned int id) {
    LogLine("[EDR-REG-START] RegsiterStartTransactionId ms=%p id=%u", ms, id);
    InterlockedExchange64(&g_lastEdrMs, (LONG64)ms);
    InterlockedIncrement(&g_edrTxCount);
    TrackMsSeen(ms);
    if (InterlockedCompareExchange(&g_edrStackTrace, 0, 0) != 0) {
        LogStack("[EDR-REG-START-STK]");
    }
    if (g_origEdrRegisterStart) {
        __try { g_origEdrRegisterStart(ms, id); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[EDR-REG-START] trampoline SEH"); }
    }
}

// ---------------------------------------------------------------------------
// CC pipeline hooks in EM_ECC - discover which CWizInterface entry point
// the Complete Compare + Apply-to-Right flow actually uses.
// ---------------------------------------------------------------------------
typedef int (__cdecl* EccWiz2MsBoolFn)(void* ms1, void* ms2, bool b1, bool b2, const void* cstr);
typedef int (__cdecl* EccWiz2MsFn)(void* ms1, void* ms2);
static EccWiz2MsBoolFn g_origShowERwinCCWiz   = nullptr;
static EccWiz2MsFn     g_origShowConflictRes  = nullptr;
static EccWiz2MsFn     g_origVSDBInteractive  = nullptr;
static EccWiz2MsFn     g_origVSDBSilentUpdate = nullptr;

// Captured CString pointer from a prior real ShowERwinCCWiz call. MFC CString
// is passed as a hidden-pointer arg; the pointed-to 8 bytes hold m_pszData
// (points to heap-allocated char buffer prefixed by a CStringData header).
// By grabbing this pointer we can re-use a valid CString for programmatic
// invocation without constructing MFC internals from scratch.
static volatile LONG64 g_capturedCStrPtr = 0;
// MFC's static empty-CString data pointer (lives in mfc140.dll; always valid).
// Captured from ShowERwinCCWiz hook when cstr.data points at the sentinel.
static volatile LONG64 g_mfcEmptyCStrData = 0;

static int __cdecl ShowERwinCCWizHook(void* ms1, void* ms2, bool b1, bool b2, const void* cstr) {
    LogLine("[CC-PIPE] ShowERwinCCWiz ENTER ms1=%p ms2=%p b1=%d b2=%d cstr=%p", ms1, ms2, (int)b1, (int)b2, cstr);
    if (cstr) {
        __try {
            const char* data = *(const char**)cstr;
            LogLine("[CC-PIPE] cstr->data=%p '%.64s'", data, data ? data : "(null)");
            // Save the inner data pointer. If it's empty, it's probably the
            // MFC static sentinel at an mfc140.dll address (always valid).
            if (data && data[0] == '\0') {
                InterlockedExchange64(&g_mfcEmptyCStrData, (LONG64)data);
                LogLine("[CC-PIPE] captured MFC empty-CString sentinel @ %p", data);
            }
            if (data) {
                // Raw 24-byte dump of the CStringData header.
                const BYTE* hp = (const BYTE*)data - 24;
                LogLine("[CC-PIPE] cstr hdr[-24..]: "
                    "%02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X "
                    "%02X%02X%02X%02X %02X%02X%02X%02X %02X%02X%02X%02X",
                    hp[0],hp[1],hp[2],hp[3],hp[4],hp[5],hp[6],hp[7],
                    hp[8],hp[9],hp[10],hp[11],hp[12],hp[13],hp[14],hp[15],
                    hp[16],hp[17],hp[18],hp[19],hp[20],hp[21],hp[22],hp[23]);
            }
            InterlockedExchange64(&g_capturedCStrPtr, (LONG64)cstr);
        } __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-PIPE] cstr decode SEH"); }
    }
    int rv = -1;
    if (g_origShowERwinCCWiz) {
        __try { rv = g_origShowERwinCCWiz(ms1, ms2, b1, b2, cstr); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-PIPE] ShowERwinCCWiz SEH"); }
    }
    LogLine("[CC-PIPE] ShowERwinCCWiz EXIT rv=%d", rv);
    return rv;
}
static int __cdecl ShowConflictResolutionUIHook(void* ms1, void* ms2) {
    LogLine("[CC-PIPE] ShowConflictResolutionUI ENTER ms1=%p ms2=%p", ms1, ms2);
    int rv = -1;
    if (g_origShowConflictRes) {
        __try { rv = g_origShowConflictRes(ms1, ms2); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-PIPE] ShowConflictRes SEH"); }
    }
    LogLine("[CC-PIPE] ShowConflictResolutionUI EXIT rv=%d", rv);
    return rv;
}
static int __cdecl VSDBInteractiveMergeHook(void* ms1, void* ms2) {
    LogLine("[CC-PIPE] VSDBInteractiveMerge ENTER ms1=%p ms2=%p", ms1, ms2);
    int rv = -1;
    if (g_origVSDBInteractive) {
        __try { rv = g_origVSDBInteractive(ms1, ms2); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-PIPE] VSDBInteractive SEH"); }
    }
    LogLine("[CC-PIPE] VSDBInteractiveMerge EXIT rv=%d", rv);
    return rv;
}
static int __cdecl VSDBSilentUpdateHook(void* ms1, void* ms2) {
    LogLine("[CC-PIPE] VSDBSilentUpdate ENTER ms1=%p ms2=%p", ms1, ms2);
    int rv = -1;
    if (g_origVSDBSilentUpdate) {
        __try { rv = g_origVSDBSilentUpdate(ms1, ms2); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[CC-PIPE] VSDBSilentUpdate SEH"); }
    }
    LogLine("[CC-PIPE] VSDBSilentUpdate EXIT rv=%d", rv);
    return rv;
}

// Exported: directly call ShowERwinCCWiz with caller-supplied args. Use the
// trampoline (g_origShowERwinCCWiz) to bypass our own hook and avoid
// recursion. Goal: test if (ms1=v3, ms2=v1, ...) runs the CC engine
// silently / without further UI clicks.
extern "C" __declspec(dllexport) int __cdecl CCInsp_CallShowERwinCCWiz(
    void* ms1, void* ms2, int b1, int b2)
{
    if (!g_origShowERwinCCWiz) {
        LogLine("[CC-PIPE-CALL] ShowERwinCCWiz trampoline not installed yet");
        return -9999;
    }
    if (!ms1 || !ms2) {
        LogLine("[CC-PIPE-CALL] ms1 or ms2 is null");
        return -9998;
    }
    // Build a valid-looking CString by reusing MFC's static empty-string
    // sentinel (captured from a prior real ShowERwinCCWiz hook fire).
    // m_pszData must point to a char buffer that is preceded by a valid
    // CStringData header; MFC's sentinel satisfies both conditions because
    // it lives inside mfc140.dll with a proper header.
    void* sentinel = (void*)InterlockedCompareExchange64(&g_mfcEmptyCStrData, 0, 0);
    if (!sentinel) {
        LogLine("[CC-PIPE-CALL] no MFC empty-CString sentinel captured yet");
        LogLine("[CC-PIPE-CALL]   Fire ShowERwinCCWiz once via Actions -> Complete Compare");
        LogLine("[CC-PIPE-CALL]   so the hook captures a valid sentinel, then retry.");
        return -9995;
    }
    struct { void* p; } cstr; cstr.p = sentinel;

    LogLine("[CC-PIPE-CALL] ShowERwinCCWiz(ms1=%p, ms2=%p, b1=%d, b2=%d, cstr=%p) ENTER",
        ms1, ms2, b1, b2, (void*)&cstr);
    int rv = -9997;
    __try { rv = g_origShowERwinCCWiz(ms1, ms2, b1 != 0, b2 != 0, &cstr); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[CC-PIPE-CALL] SEH 0x%08lX", GetExceptionCode());
        return -9996;
    }
    LogLine("[CC-PIPE-CALL] ShowERwinCCWiz returned rv=%d", rv);
    return rv;
}

static void InstallEccPipelineHooks(void) {
    HMODULE ecc = GetModuleHandleW(L"EM_ECC.dll");
    if (!ecc) ecc = LoadLibraryW(L"EM_ECC.dll");
    if (!ecc) { LogLine("[CC-PIPE] EM_ECC not loaded"); return; }

    struct { const char* sym; void* hook; void** origSlot; const char* name; } hooks[] = {
        { "?ShowERwinCCWiz@CWizInterface@@SAHPEAVGDMModelSetI@@0_N1V?$CStringT@DV?$StrTraitMFC_DLL@DV?$ChTraitsCRT@D@ATL@@@@@ATL@@@Z",
          (void*)&ShowERwinCCWizHook, (void**)&g_origShowERwinCCWiz, "ShowERwinCCWiz" },
        { "?ShowConflictResolutionUI@CWizInterface@@SAHPEAVGDMModelSetI@@0@Z",
          (void*)&ShowConflictResolutionUIHook, (void**)&g_origShowConflictRes, "ShowConflictResolutionUI" },
        { "?VSDBInteractiveMerge@CWizInterface@@SAHPEAVGDMModelSetI@@0@Z",
          (void*)&VSDBInteractiveMergeHook, (void**)&g_origVSDBInteractive, "VSDBInteractiveMerge" },
        { "?VSDBSilentUpdate@CWizInterface@@SAHPEAVGDMModelSetI@@0@Z",
          (void*)&VSDBSilentUpdateHook, (void**)&g_origVSDBSilentUpdate, "VSDBSilentUpdate" },
    };
    for (const auto& h : hooks) {
        void* target = GetProcAddress(ecc, h.sym);
        if (!target) { LogLine("[CC-PIPE] %s symbol NOT FOUND", h.name); continue; }
        void* tramp = nullptr;
        if (InstallInlineHook(target, h.hook, &tramp)) {
            *h.origSlot = tramp;
            LogLine("[CC-PIPE] %s installed: target=%p tramp=%p", h.name, target, tramp);
        } else {
            LogLine("[CC-PIPE] %s install FAILED", h.name);
        }
    }
}

// Exported: toggle EDR stack-trace logging. Turn ON just before pressing
// Apply-to-Right manually, so we can see which ELC2 function is calling
// EDRAlterNameCaching::RegsiterStartTransactionId.
extern "C" __declspec(dllexport) void __cdecl CCInsp_SetEdrStackTrace(int enable) {
    InterlockedExchange(&g_edrStackTrace, enable ? 1 : 0);
    LogLine("[EDR-REG-START] stack-trace mode = %s", enable ? "ON" : "OFF");
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_InstallEdrHooks(void) {
    HMODULE edr = GetModuleHandleW(L"EM_EDR.dll");
    if (!edr) edr = LoadLibraryW(L"EM_EDR.dll");
    if (!edr) return -1;

    void* tgt1 = GetProcAddress(edr, kEdrRegisterSym);
    if (tgt1 && !g_origEdrRegister) {
        void* tramp = nullptr;
        if (InstallInlineHook(tgt1, (void*)&EdrRegisterHook, &tramp)) {
            g_origEdrRegister = (EdrRegisterFn)tramp;
            LogLine("[EDR-HOOK] Register installed: target=%p tramp=%p", tgt1, tramp);
        } else LogLine("[EDR-HOOK] Register install FAILED");
    }
    void* tgt2 = GetProcAddress(edr, kEdrRegisterStartSym);
    if (tgt2 && !g_origEdrRegisterStart) {
        void* tramp = nullptr;
        if (InstallInlineHook(tgt2, (void*)&EdrRegisterStartHook, &tramp)) {
            g_origEdrRegisterStart = (EdrRegisterStartFn)tramp;
            LogLine("[EDR-HOOK] RegsiterStartTransactionId installed: target=%p tramp=%p", tgt2, tramp);
        } else LogLine("[EDR-HOOK] RegsiterStartTransactionId install FAILED");
    }
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_GetEdrTxCount(void) {
    return (int)InterlockedCompareExchange(&g_edrTxCount, 0, 0);
}

// Directly invoke CWizInterface::ApplyDifferencesToRight(leftMs, rightMs).
// Bypasses the XTP custom listview click synthesis entirely - calls the
// exported EM_ECC function through its trampoline (i.e. the original,
// un-hooked entry). Returns the int result of the original function, or
// -9999 if the trampoline isn't available. SEH-safe.
extern "C" __declspec(dllexport) int __cdecl CCInsp_CallApplyDifferencesToRight(void* leftMs, void* rightMs) {
    if (!g_origApplyDiffRight) {
        LogLine("[ADR-CALL] g_origApplyDiffRight not bound - hook may have failed");
        return -9999;
    }
    LogLine("[ADR-CALL] invoking ApplyDifferencesToRight(ms1=%p, ms2=%p) directly", leftMs, rightMs);
    int rv = -1;
    __try {
        rv = g_origApplyDiffRight(leftMs, rightMs);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[ADR-CALL] SEH 0x%08X", GetExceptionCode());
        return -8888;
    }
    LogLine("[ADR-CALL] returned rv=%d", rv);
    return rv;
}

extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetLastEdrMs(void) {
    return (void*)InterlockedCompareExchange64(&g_lastEdrMs, 0, 0);
}

// Exported: return the Nth distinct ms seen (0-based). N=0 gives the first
// ms (usually the active / left model, v3). N=1 gives the second (v1 from
// Mart -> Open). Returns null if N >= count.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetSeenMs(int index) {
    if (index < 0 || index >= (int)kMaxSeenMs) return nullptr;
    if (index >= InterlockedCompareExchange(&g_seenMsCount, 0, 0)) return nullptr;
    return (void*)InterlockedCompareExchange64(&g_seenMs[index], 0, 0);
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_GetSeenMsCount(void) {
    return InterlockedCompareExchange(&g_seenMsCount, 0, 0);
}

// PSM probe: calls PrepareServerModelSet(ms, &as1) under SEH, logs result.
// Used to determine if the F2/MCX native pipeline is reachable for a given
// mart-bound ms (without committing to a full MCX::Execute attempt).
//
// Return semantics:
//   1  = success, as1 returned non-null (F2 path is open for this ms)
//   0  = PSM ran but returned null/null-as (F2 not viable with this ms)
//  -1  = invalid input or PSM symbol unresolved
//  -2  = SEH during PSM call (mart state likely missing)
extern "C" __declspec(dllexport) int __cdecl CCInsp_TestPSM(void* ms) {
    if (!ms) {
        LogLine("[PSM-PROBE] ms is null - nothing to probe");
        return -1;
    }
    if (!g_prepareServer) {
        HMODULE mcx = GetModuleHandleW(L"EM_MCX.dll");
        if (!mcx) mcx = LoadLibraryW(L"EM_MCX.dll");
        if (mcx) g_prepareServer = (PrepareServerFn)GetProcAddress(mcx, kPrepareServerSym);
    }
    if (!g_prepareServer) {
        LogLine("[PSM-PROBE] PrepareServerModelSet symbol unresolved");
        return -1;
    }
    void* outAs = nullptr;
    void* serverMsOut = nullptr;
    LogLine("[PSM-PROBE] calling PrepareServerModelSet(ms=%p, &outAs)...", ms);
    __try {
        serverMsOut = g_prepareServer(ms, &outAs);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[PSM-PROBE] SEH 0x%08lX during PSM call (mart state likely missing)", GetExceptionCode());
        return -2;
    }
    LogLine("[PSM-PROBE] returned: serverMs=%p outAs=%p", serverMsOut, outAs);
    if (outAs) {
        LogLine("[PSM-PROBE] SUCCESS: as1 returned, F2/MCX path is OPEN for this ms");
        return 1;
    }
    LogLine("[PSM-PROBE] FAILED: outAs is null - F2 path not viable with this ms");
    return 0;
}

static const char* kElaOnFeSym =
    "?OnFE@ELA@@YA?AW4Success_e@@PEAVGDMModelSetI@@_NI@Z";

// Detour hook: log every OnFE call's args. Lets us see what flags value the
// 'Right Alter Script' button handler actually passes, vs our programmatic
// call which currently passes 0 (producing full-schema DDL instead of alter).
static int __cdecl ElaOnFeHook(void* ms, bool flag, unsigned int flags) {
    LogLine("[ONFE-HOOK] ENTER ms=%p bool=%d flags=%u (0x%X)", ms, (int)flag, flags, flags);
    // Record the ms so the Mart-Mart orchestrator can later reuse it as
    // the RIGHT-side model for a programmatic OnFE call.
    InterlockedExchange64(&g_lastOnFeMs, (LONG64)ms);
    int rv = -1;
    if (g_origElaOnFe) {
        __try { rv = g_origElaOnFe(ms, flag, flags); }
        __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("[ONFE-HOOK] trampoline SEH 0x%08lX", GetExceptionCode()); }
    }
    LogLine("[ONFE-HOOK] EXIT rv=%d", rv);
    return rv;
}

// Exported: return the most-recent GDMModelSetI* that was passed to ELA::OnFE
// (captured by our detour). Null until at least one OnFE call has happened.
extern "C" __declspec(dllexport) void* __cdecl CCInsp_GetLastOnFeMs(void) {
    return (void*)InterlockedCompareExchange64(&g_lastOnFeMs, 0, 0);
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_InstallOnFeHook(void) {
    HMODULE ela = GetModuleHandleW(L"EM_ELA.dll");
    if (!ela) ela = LoadLibraryW(L"EM_ELA.dll");
    if (!ela) return -1;
    void* target = GetProcAddress(ela, kElaOnFeSym);
    if (!target) { LogLine("[ONFE-HOOK] OnFE symbol not found"); return -2; }
    if (g_origElaOnFe) { LogLine("[ONFE-HOOK] already installed"); return 1; }
    void* tramp = nullptr;
    if (!InstallInlineHook(target, (void*)&ElaOnFeHook, &tramp)) {
        LogLine("[ONFE-HOOK] InstallInlineHook failed");
        return -3;
    }
    g_origElaOnFe = (ElaOnFeFn)tramp;
    // Replace g_elaOnFe with the trampoline so our own CallOnFE paths
    // bypass the hook (avoid infinite recursion).
    g_elaOnFe = (ElaOnFeFn)tramp;
    LogLine("[ONFE-HOOK] installed: target=%p tramp=%p", target, tramp);
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_CallOnFE(
    void* ms, int boolFlag, unsigned int flags)
{
    if (!g_elaOnFe) {
        HMODULE ela = GetModuleHandleW(L"EM_ELA.dll");
        if (!ela) ela = LoadLibraryW(L"EM_ELA.dll");
        if (ela) {
            g_elaOnFe = (ElaOnFeFn)GetProcAddress(ela, kElaOnFeSym);
            LogLine("[CC-INSP] ELA OnFE addr=%p", (void*)g_elaOnFe);
        }
    }
    if (!g_elaOnFe) { LogLine("[CC-INSP] ELA::OnFE unresolved"); return -9998; }
    if (!ms) { LogLine("[CC-INSP] ELA::OnFE: ms is null"); return -9997; }
    LogLine("[CC-INSP] ELA::OnFE(ms=%p, bool=%d, flags=%u) ENTER", ms, boolFlag, flags);
    int rv = -9999;
    __try { rv = g_elaOnFe(ms, boolFlag != 0, flags); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[CC-INSP] ELA::OnFE SEH 0x%08lX", GetExceptionCode());
        return -9996;
    }
    LogLine("[CC-INSP] ELA::OnFE EXIT rv=%d", rv);
    return rv;
}

// Worker thread context for the OnFE+InvokePreview orchestration.
struct OnFeWorkerCtx {
    HWND mainHwnd;
};

// SEH-guarded helper so OnFeWorkerProc (which uses C++ destructors/new/delete)
// can still call the unsafe Invoke without tripping C2712.
static void InvokePreviewSeh(void* fewpo) {
    __try {
        typedef void (__cdecl* InvokeSingleArgFn)(void* self);
        ((InvokeSingleArgFn)g_directInvokePreview)(fewpo);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[ONFE-WORKER] Invoke post-return SEH 0x%08lX (ignored, DDL already captured)", GetExceptionCode());
    }
}

// Similar SEH-guarded helper for OnFE itself.
static int CallOnFeSeh(ElaOnFeFn fn, void* ms, bool b, unsigned int f) {
    __try { return fn(ms, b, f); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[ONFE-ORCH] OnFE SEH 0x%08lX", GetExceptionCode());
        return -1;
    }
}

static DWORD WINAPI OnFeWorkerProc(LPVOID lp) {
    OnFeWorkerCtx* ctx = (OnFeWorkerCtx*)lp;
    LogLine("[ONFE-WORKER] started");

    // Phase 1: wait for FEWPageOptions ctor to fire (signals wizard opened).
    DWORD start = GetTickCount();
    void* fewpo = nullptr;
    while (GetTickCount() - start < 15000) {
        fewpo = (void*)InterlockedCompareExchange64(&g_capturedFEWPO, 0, 0);
        if (fewpo) break;
        Sleep(20);
    }
    if (!fewpo) {
        LogLine("[ONFE-WORKER] timeout - FEW-CTOR never fired");
        delete ctx;
        return 1;
    }
    LogLine("[ONFE-WORKER] FEWPO captured = %p", fewpo);

    // Phase 2: give the wizard a moment to finish init (FEWPagePreviewEx ctor,
    // OnInitDialog, gbl_pxAs write). 100ms is generous.
    Sleep(100);

    // Phase 3: call InvokePreviewStringOnlyCommand on captured FEWPO. This
    // internally drives the CC-context Preview, calls GenerateAlterScript,
    // our GA detour captures the DDL into g_lastCapturedDdl.
    if (!g_directInvokePreview) {
        LogLine("[ONFE-WORKER] g_directInvokePreview not resolved");
        delete ctx;
        return 2;
    }
    LogLine("[ONFE-WORKER] calling Invoke on FEWPO=%p", fewpo);
    InvokePreviewSeh(fewpo);
    LogLine("[ONFE-WORKER] Invoke returned");

    // Phase 4: close the wizard so OnFE returns. Enumerate top-level windows
    // belonging to erwin, find the Alter Script wizard, send WM_COMMAND IDCANCEL.
    auto dialogs = EnumerateVisibleDialogs();
    for (HWND h : dialogs) {
        if (LooksLikeAlterScriptWizard(h)) {
            LogLine("[ONFE-WORKER] posting IDCANCEL to wizard hwnd=%p", h);
            PostMessage(h, WM_COMMAND, MAKEWPARAM(IDCANCEL, BN_CLICKED), 0);
            PostMessage(h, WM_CLOSE, 0, 0);
        }
    }
    LogLine("[ONFE-WORKER] done");
    delete ctx;
    return 0;
}

// Orchestrated Mart-Mart DDL via ELA::OnFE.
// Arms FEW-CTOR watcher in a worker thread, calls OnFE (blocks on modal on
// this thread), worker drives wizard to Preview and closes it, OnFE returns,
// we return whatever DDL was captured.
extern "C" __declspec(dllexport) const char* __cdecl CCInsp_GenerateMartMartDdlViaOnFE(void* ms) {
    // If caller did not pass an explicit ms, fall back to whatever the
    // last seen OnFE call recorded (g_lastOnFeMs). When the caller does
    // pass an ms (Mart-Mart with v1 PU), respect it - do NOT override
    // with stale g_lastOnFeMs from a prior dirty-vs-saved test.
    if (!ms) {
        void* onFeMs = (void*)InterlockedCompareExchange64(&g_lastOnFeMs, 0, 0);
        if (onFeMs) {
            LogLine("[ONFE-ORCH] caller ms=null, falling back to g_lastOnFeMs=%p", onFeMs);
            ms = onFeMs;
        }
    } else {
        LogLine("[ONFE-ORCH] caller passed explicit ms=%p (g_lastOnFeMs ignored)", ms);
    }
    if (!ms) { LogLine("[ONFE-ORCH] ms is null (no caller ms and g_lastOnFeMs empty)"); return nullptr; }
    if (!g_elaOnFe) {
        HMODULE ela = GetModuleHandleW(L"EM_ELA.dll");
        if (!ela) ela = LoadLibraryW(L"EM_ELA.dll");
        if (ela) g_elaOnFe = (ElaOnFeFn)GetProcAddress(ela, kElaOnFeSym);
    }
    if (!g_elaOnFe) { LogLine("[ONFE-ORCH] OnFE unresolved"); return nullptr; }
    if (!g_directInvokePreview) { LogLine("[ONFE-ORCH] InvokePreview unresolved"); return nullptr; }

    HWND mainHwnd = FindErwinMain();
    LogLine("[ONFE-ORCH] starting - erwin main=%p ms=%p", (void*)mainHwnd, ms);

    // Reset state so the worker sees a fresh ctor fire.
    InterlockedExchange64(&g_capturedFEWPO, 0);
    InterlockedExchange64(&g_capturedFEWPreviewEx, 0);
    InterlockedExchange(&g_autoOpenCtorFired, 0);
    ClearCapturedDdl();   // drop any prior DDL

    // Install WinEvent hook so the wizard window is hidden as soon as it's
    // created (flash-free, same mechanism as OpenAlterScriptWizardHidden).
    InterlockedExchange64(&g_hiddenWizardHwnd, 0);
    HWINEVENTHOOK evHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
        nullptr, WizardWinEventCb,
        GetCurrentProcessId(), 0,
        WINEVENT_OUTOFCONTEXT);
    LogLine("[ONFE-ORCH] WinEvent hook = %p", (void*)evHook);

    // Spawn worker BEFORE OnFE so it's ready when ctor fires.
    OnFeWorkerCtx* ctx = new OnFeWorkerCtx{ mainHwnd };
    HANDLE worker = CreateThread(nullptr, 0, OnFeWorkerProc, ctx, 0, nullptr);
    if (!worker) {
        LogLine("[ONFE-ORCH] CreateThread failed");
        if (evHook) UnhookWinEvent(evHook);
        delete ctx;
        return nullptr;
    }

    // Call OnFE. This blocks on CPropertySheet::DoModal until the worker
    // closes the wizard via IDCANCEL.
    // Manual 'Right Alter Script' button calls OnFE(ms, true, 0x13). These
    // args were captured via our ElaOnFeHook. With (false, 0) the flags
    // fallback kicks in and the wizard builds a full-schema CREATE DDL
    // instead of the alter/diff DDL we want.
    const unsigned int kAlterFlags = 0x13;
    const bool kAlterBool = true;
    LogLine("[ONFE-ORCH] calling ELA::OnFE(ms=%p, true, 0x13) - will block until worker closes wizard", ms);
    int rv = CallOnFeSeh(g_elaOnFe, ms, kAlterBool, kAlterFlags);
    LogLine("[ONFE-ORCH] OnFE returned rv=%d", rv);

    // Wait briefly for worker to finish cleanup.
    WaitForSingleObject(worker, 2000);
    CloseHandle(worker);
    if (evHook) UnhookWinEvent(evHook);

    // Read captured DDL. Returns heap-allocated UTF-8 string; caller must
    // free via FreeDdlBuffer.
    return (const char*)ConsumeLastCapturedDdl();
}

// Exported: write to ELC2!gbl_pxActionSummary. Use this immediately before
// invoking the Alter Script wizard (hidden or otherwise) so the wizard
// consumes our chosen AS instead of whatever the native flow would compute.
extern "C" __declspec(dllexport) int __cdecl CCInsp_SetGlobalPxAs(void* as) {
    if (!g_elc2GblAsPtrAddr) { LogLine("[CC-INSP] gbl_pxAs addr not resolved"); return -1; }
    DWORD oldProt = 0;
    if (!VirtualProtect(g_elc2GblAsPtrAddr, sizeof(void*), PAGE_READWRITE, &oldProt)) {
        LogLine("[CC-INSP] VirtualProtect for SetGlobalPxAs failed err=0x%lX", GetLastError());
        return -2;
    }
    __try { *g_elc2GblAsPtrAddr = as; }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        VirtualProtect(g_elc2GblAsPtrAddr, sizeof(void*), oldProt, &oldProt);
        LogLine("[CC-INSP] SetGlobalPxAs SEH");
        return -3;
    }
    VirtualProtect(g_elc2GblAsPtrAddr, sizeof(void*), oldProt, &oldProt);
    LogLine("[CC-INSP] gbl_pxActionSummary = %p (written)", as);
    return 0;
}

// Exported: calls CWizInterface::ApplyCCSilentMode(leftMs, rightMs, level, cstr)
// with a throwaway empty CString for the output file. Returns the function's
// int return value, or -9999 on SEH. Caller pre-dumps globals, we dump again.
// Identity-compare test: passing left==right tests whether the call path is
// viable without requiring us to own a second Mart PU's GDMModelSetI*.
extern "C" __declspec(dllexport) int __cdecl CCInsp_CallApplyCCSilent(
    void* leftMs, void* rightMs, int level)
{
    if (!g_applyCCSilent) {
        LogLine("[CC-SILENT] ApplyCCSilentMode symbol not resolved");
        return -9998;
    }
    if (!leftMs || !rightMs) {
        LogLine("[CC-SILENT] left or right ms is null");
        return -9997;
    }
    // MFC CStringT x64 layout: single pointer to a heap-allocated header +
    // char data. An empty string in MFC can be represented by a pointer to
    // the special "empty string" internal sentinel — but we don't have that.
    // Safer: construct a local buffer that looks like CString for "", which
    // is actually just a 16-bit NUL on the heap with ref-count -1. Too
    // fragile. Instead we pass the address of a 16-byte zeroed buffer — if
    // ApplyCCSilentMode reads the CString's internal pointer and dereferences
    // it, it'll find a NUL char. Worst case: SEH, which we catch.
    char emptyCStringSlot[16] = {0};
    // The typical CString x64 layout: emptyCStringSlot[0] is a ptr-to-data.
    // We want that ptr to point at a NUL byte. Set it to a static NUL.
    static const char kEmptyChar = '\0';
    *(const char**)emptyCStringSlot = &kEmptyChar;

    LogLine("[CC-SILENT] ENTER leftMs=%p rightMs=%p level=%d", leftMs, rightMs, level);
    int rv = -9999;
    __try { rv = g_applyCCSilent(leftMs, rightMs, level, (void*)emptyCStringSlot); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[CC-SILENT] SEH 0x%08lX during call", GetExceptionCode());
        return -9996;
    }
    LogLine("[CC-SILENT] EXIT rv=%d", rv);
    return rv;
}

// ---------------------------------------------------------------------------
// ECC Apply-to-Right direct hook + replay.
//
// Stack-trace diagnosis established that a manual click on the Resolve-
// Differences Model-row arrow ultimately invokes a chain:
//   EM_CMP.dll + 0x13920   (XTP listview click handler)
//     -> EM_ECC.dll + 0x42F4A  (Apply dispatcher)
//       -> EM_ECC.dll + 0x42A83 (Apply inner)
//         -> EM_GBC.dll + 0x6A77F (bridge)
//           -> EM_GBC.dll + 0x6A5B3
//             -> EM_GDM + ... + EDR Register
//
// 0x42F4A is a RETURN address, so the enclosing function starts earlier.
// We use RtlLookupFunctionEntry (PDATA unwind table) to resolve the
// function's BeginAddress, then install an inline hook there. On entry
// the hook stashes RCX/RDX/R8/R9 for later replay via
// CCInsp_ReplayEccApply().
// ---------------------------------------------------------------------------

// Latched arguments from the first hit of EccApplyHook. 0 => not yet captured.
// The target is resolved via RtlLookupFunctionEntry against EM_ECC+0x42F4A
// (return-address seen in manual-click stack traces). The enclosing
// function starts at EM_ECC+0x42EA0 in the tested r10 binary. Hooking
// this dispatcher caused 0xC0000005 AV in the trampoline path - likely
// because the function takes more than 4 register args / reads stack
// locals that our extra frames disturbed. The alternative hook at
// EM_CMP.dll + 0x13920 (one frame higher - XTP listview click handler)
// is installed instead; see CCInsp_HookCmpApply below.
static volatile LONG64 g_eccApplyArg1 = 0;   // RCX
static volatile LONG64 g_eccApplyArg2 = 0;   // RDX
static volatile LONG64 g_eccApplyArg3 = 0;   // R8
static volatile LONG64 g_eccApplyArg4 = 0;   // R9
static volatile LONG   g_eccApplyHookCount = 0;
static void* g_eccApplyTarget = nullptr;     // function start (for logging)

// CMP hook: EM_CMP.dll + 0x13920 (return address in manual-click stack).
// Function containing that RA is the XTP listview click dispatcher - one
// frame above the ECC handler. Args should be higher-level / heap-stable.
static volatile LONG64 g_cmpApplyArg1 = 0;
static volatile LONG64 g_cmpApplyArg2 = 0;
static volatile LONG64 g_cmpApplyArg3 = 0;
static volatile LONG64 g_cmpApplyArg4 = 0;
static volatile LONG   g_cmpApplyHookCount = 0;
static void* g_cmpApplyTarget = nullptr;
typedef LONG64 (__fastcall* CmpApplyFn)(void*, void*, void*, void*);
static CmpApplyFn g_origCmpApply = nullptr;

static LONG64 __fastcall CmpApplyHook(void* a1, void* a2, void* a3, void* a4) {
    LONG count = InterlockedIncrement(&g_cmpApplyHookCount);
    LogLine("[CMP-APPLY-HOOK] #%ld ENTER rcx=%p rdx=%p r8=%p r9=%p",
        count, a1, a2, a3, a4);
    if (count == 1) {
        InterlockedExchange64(&g_cmpApplyArg1, (LONG64)a1);
        InterlockedExchange64(&g_cmpApplyArg2, (LONG64)a2);
        InterlockedExchange64(&g_cmpApplyArg3, (LONG64)a3);
        InterlockedExchange64(&g_cmpApplyArg4, (LONG64)a4);
        LogLine("[CMP-APPLY-HOOK] args LATCHED for replay");
    }
    LONG64 rv = 0;
    if (g_origCmpApply) {
        __try { rv = g_origCmpApply(a1, a2, a3, a4); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[CMP-APPLY-HOOK] trampoline SEH 0x%08lX", GetExceptionCode());
        }
    }
    LogLine("[CMP-APPLY-HOOK] #%ld EXIT rv=0x%llX", count, (unsigned long long)rv);
    return rv;
}

// int __fastcall F(rcx, rdx, r8, r9)  - we don't know the real signature,
// but capturing 4 pointer-sized args covers the x64 fastcall register set.
typedef LONG64 (__fastcall* EccApplyFn)(void*, void*, void*, void*);
static EccApplyFn g_origEccApply = nullptr;

static LONG64 __fastcall EccApplyHook(void* a1, void* a2, void* a3, void* a4) {
    LONG count = InterlockedIncrement(&g_eccApplyHookCount);
    LogLine("[ECC-APPLY-HOOK] #%ld ENTER rcx=%p rdx=%p r8=%p r9=%p",
        count, a1, a2, a3, a4);
    // Only stash on the FIRST hit - subsequent hits would clobber the
    // latched replay context.
    if (count == 1) {
        InterlockedExchange64(&g_eccApplyArg1, (LONG64)a1);
        InterlockedExchange64(&g_eccApplyArg2, (LONG64)a2);
        InterlockedExchange64(&g_eccApplyArg3, (LONG64)a3);
        InterlockedExchange64(&g_eccApplyArg4, (LONG64)a4);
        LogLine("[ECC-APPLY-HOOK] args LATCHED for replay");
    }
    LONG64 rv = 0;
    if (g_origEccApply) {
        __try { rv = g_origEccApply(a1, a2, a3, a4); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[ECC-APPLY-HOOK] trampoline SEH 0x%08lX", GetExceptionCode());
        }
    }
    LogLine("[ECC-APPLY-HOOK] #%ld EXIT rv=0x%llX", count, (unsigned long long)rv);
    return rv;
}

// Uses RtlLookupFunctionEntry to find the function containing `addr` and
// returns its start. Returns nullptr if no unwind info covers the address.
static void* ResolveFunctionStart(void* addr) {
    DWORD64 imageBase = 0;
    PRUNTIME_FUNCTION rf = RtlLookupFunctionEntry((DWORD64)addr, &imageBase, nullptr);
    if (!rf || !imageBase) return nullptr;
    return (void*)(imageBase + rf->BeginAddress);
}

// Install the hook on the function containing EM_ECC.dll + 0x42F4A.
// Returns 0 on success, negative on failure. Idempotent.
extern "C" __declspec(dllexport) int __cdecl CCInsp_HookEccApply(void) {
    if (g_eccApplyTarget != nullptr) {
        LogLine("[ECC-APPLY-HOOK] already installed at %p", g_eccApplyTarget);
        return 0;
    }
    HMODULE ecc = GetModuleHandleW(L"EM_ECC.dll");
    if (!ecc) {
        LogLine("[ECC-APPLY-HOOK] EM_ECC.dll not loaded");
        return -1;
    }
    void* returnAddr = (BYTE*)ecc + 0x42F4A;
    // RtlLookupFunctionEntry wants an address WITHIN the function. The
    // return address 0x42F4A is inside the enclosing function, so this is
    // the right pointer to query.
    void* fnStart = ResolveFunctionStart(returnAddr);
    if (!fnStart) {
        LogLine("[ECC-APPLY-HOOK] RtlLookupFunctionEntry returned null for EM_ECC+0x42F4A (%p)", returnAddr);
        return -2;
    }
    LogLine("[ECC-APPLY-HOOK] target function start resolved: %p (EM_ECC+0x%llX)",
        fnStart, (unsigned long long)((BYTE*)fnStart - (BYTE*)ecc));

    void* tramp = nullptr;
    if (!InstallInlineHook(fnStart, (void*)&EccApplyHook, &tramp)) {
        LogLine("[ECC-APPLY-HOOK] InstallInlineHook FAILED");
        return -3;
    }
    g_origEccApply = (EccApplyFn)tramp;
    g_eccApplyTarget = fnStart;
    LogLine("[ECC-APPLY-HOOK] installed ok. trampoline=%p", tramp);
    return 0;
}

// Returns 1 if args have been latched by the hook, 0 otherwise. Out-params
// receive the latched a1..a4 (may be null-ish if no hit). Safe to poll.
extern "C" __declspec(dllexport) int __cdecl CCInsp_GetEccApplyArgs(
    void** out1, void** out2, void** out3, void** out4)
{
    if (out1) *out1 = (void*)InterlockedCompareExchange64(&g_eccApplyArg1, 0, 0);
    if (out2) *out2 = (void*)InterlockedCompareExchange64(&g_eccApplyArg2, 0, 0);
    if (out3) *out3 = (void*)InterlockedCompareExchange64(&g_eccApplyArg3, 0, 0);
    if (out4) *out4 = (void*)InterlockedCompareExchange64(&g_eccApplyArg4, 0, 0);
    return (InterlockedCompareExchange(&g_eccApplyHookCount, 0, 0) > 0) ? 1 : 0;
}

// Replay: call the original Apply function with previously-latched args
// (or explicit args if any supplied are non-null; nullptr means "use
// latched value"). Returns the function's result, or sentinel on error.
extern "C" __declspec(dllexport) LONG64 __cdecl CCInsp_ReplayEccApply(
    void* overrideA1, void* overrideA2, void* overrideA3, void* overrideA4)
{
    if (!g_origEccApply) {
        LogLine("[ECC-APPLY-REPLAY] no trampoline - hook not installed");
        return (LONG64)-9999;
    }
    void* a1 = overrideA1 ? overrideA1 : (void*)InterlockedCompareExchange64(&g_eccApplyArg1, 0, 0);
    void* a2 = overrideA2 ? overrideA2 : (void*)InterlockedCompareExchange64(&g_eccApplyArg2, 0, 0);
    void* a3 = overrideA3 ? overrideA3 : (void*)InterlockedCompareExchange64(&g_eccApplyArg3, 0, 0);
    void* a4 = overrideA4 ? overrideA4 : (void*)InterlockedCompareExchange64(&g_eccApplyArg4, 0, 0);
    LogLine("[ECC-APPLY-REPLAY] invoking with a1=%p a2=%p a3=%p a4=%p", a1, a2, a3, a4);
    LONG64 rv = 0;
    __try { rv = g_origEccApply(a1, a2, a3, a4); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[ECC-APPLY-REPLAY] SEH 0x%08lX", GetExceptionCode());
        return (LONG64)-8888;
    }
    LogLine("[ECC-APPLY-REPLAY] returned rv=0x%llX", (unsigned long long)rv);
    return rv;
}

// --- MSGMAP variant: hook at EM_CMP.dll + 0x1EA11's enclosing function
// (MFC AFX_MSGMAP_ENTRY handler - highest erwin frame, called directly by
// mfc140's dispatcher). Args here are MFC-handler-style (this, wp, lp,
// pResult) or notification-style (this, NMHDR*, pResult) - both heap-
// backed, more likely to survive replay than ECC's stack-pointer args. ---

static volatile LONG64 g_msgMapArg1 = 0;
static volatile LONG64 g_msgMapArg2 = 0;
static volatile LONG64 g_msgMapArg3 = 0;
static volatile LONG64 g_msgMapArg4 = 0;
static volatile LONG   g_msgMapHookCount = 0;
static void* g_msgMapTarget = nullptr;
typedef LONG64 (__fastcall* MsgMapFn)(void*, void*, void*, void*);
static MsgMapFn g_origMsgMap = nullptr;

// Hex-dump up to 32 bytes at `addr`, gracefully handling bad pointers.
static void DumpAt(const char* label, void* addr) {
    if (!addr) { LogLine("  %s: (null)", label); return; }
    __try {
        BYTE* p = (BYTE*)addr;
        LogLine("  %s @ %p: %02X %02X %02X %02X %02X %02X %02X %02X  %02X %02X %02X %02X %02X %02X %02X %02X",
            label, addr,
            p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7],
            p[8], p[9], p[10], p[11], p[12], p[13], p[14], p[15]);
        LogLine("  %s @ %p: %02X %02X %02X %02X %02X %02X %02X %02X  %02X %02X %02X %02X %02X %02X %02X %02X",
            label, (BYTE*)addr + 16,
            p[16], p[17], p[18], p[19], p[20], p[21], p[22], p[23],
            p[24], p[25], p[26], p[27], p[28], p[29], p[30], p[31]);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { LogLine("  %s @ %p: AV on read", label, addr); }
}

static LONG64 __fastcall MsgMapHook(void* a1, void* a2, void* a3, void* a4) {
    LONG count = InterlockedIncrement(&g_msgMapHookCount);
    LogLine("[MSGMAP-HOOK] #%ld ENTER rcx=%p rdx=%p r8=%p r9=%p",
        count, a1, a2, a3, a4);
    if (count == 1) {
        InterlockedExchange64(&g_msgMapArg1, (LONG64)a1);
        InterlockedExchange64(&g_msgMapArg2, (LONG64)a2);
        InterlockedExchange64(&g_msgMapArg3, (LONG64)a3);
        InterlockedExchange64(&g_msgMapArg4, (LONG64)a4);
        LogLine("[MSGMAP-HOOK] args LATCHED for replay");
        // Dump 32 bytes at each pointer - args appear to be refs to caller's
        // stack locals (likely CWnd*, WPARAM, LPARAM, LRESULT* layout used by
        // MFC's AfxDispatchCall). The data at these addresses should include
        // an NMHDR (hwndFrom, idFrom, code) we can replay via WM_NOTIFY.
        DumpAt("*rcx", a1);
        DumpAt("*rdx", a2);
        DumpAt("*r8 ", a3);
        DumpAt("*r9 ", a4);
    }
    LONG64 rv = 0;
    if (g_origMsgMap) {
        __try { rv = g_origMsgMap(a1, a2, a3, a4); }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LogLine("[MSGMAP-HOOK] trampoline SEH 0x%08lX", GetExceptionCode());
        }
    }
    LogLine("[MSGMAP-HOOK] #%ld EXIT rv=0x%llX", count, (unsigned long long)rv);
    return rv;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_HookMsgMap(void) {
    if (g_msgMapTarget != nullptr) {
        LogLine("[MSGMAP-HOOK] already installed at %p", g_msgMapTarget);
        return 0;
    }
    HMODULE cmp = GetModuleHandleW(L"EM_CMP.dll");
    if (!cmp) { LogLine("[MSGMAP-HOOK] EM_CMP.dll not loaded"); return -1; }
    void* returnAddr = (BYTE*)cmp + 0x1EA11;
    void* fnStart = ResolveFunctionStart(returnAddr);
    if (!fnStart) {
        LogLine("[MSGMAP-HOOK] RtlLookupFunctionEntry null for EM_CMP+0x1EA11 (%p)", returnAddr);
        return -2;
    }
    LogLine("[MSGMAP-HOOK] target function start resolved: %p (EM_CMP+0x%llX)",
        fnStart, (unsigned long long)((BYTE*)fnStart - (BYTE*)cmp));
    void* tramp = nullptr;
    if (!InstallInlineHook(fnStart, (void*)&MsgMapHook, &tramp)) {
        LogLine("[MSGMAP-HOOK] InstallInlineHook FAILED");
        return -3;
    }
    g_origMsgMap = (MsgMapFn)tramp;
    g_msgMapTarget = fnStart;
    LogLine("[MSGMAP-HOOK] installed ok. trampoline=%p", tramp);
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_GetMsgMapArgs(
    void** out1, void** out2, void** out3, void** out4)
{
    if (out1) *out1 = (void*)InterlockedCompareExchange64(&g_msgMapArg1, 0, 0);
    if (out2) *out2 = (void*)InterlockedCompareExchange64(&g_msgMapArg2, 0, 0);
    if (out3) *out3 = (void*)InterlockedCompareExchange64(&g_msgMapArg3, 0, 0);
    if (out4) *out4 = (void*)InterlockedCompareExchange64(&g_msgMapArg4, 0, 0);
    return (InterlockedCompareExchange(&g_msgMapHookCount, 0, 0) > 0) ? 1 : 0;
}

extern "C" __declspec(dllexport) LONG64 __cdecl CCInsp_ReplayMsgMap(
    void* a1, void* a2, void* a3, void* a4)
{
    if (!g_origMsgMap) { LogLine("[MSGMAP-REPLAY] no trampoline"); return -9999; }
    void* x1 = a1 ? a1 : (void*)InterlockedCompareExchange64(&g_msgMapArg1, 0, 0);
    void* x2 = a2 ? a2 : (void*)InterlockedCompareExchange64(&g_msgMapArg2, 0, 0);
    void* x3 = a3 ? a3 : (void*)InterlockedCompareExchange64(&g_msgMapArg3, 0, 0);
    void* x4 = a4 ? a4 : (void*)InterlockedCompareExchange64(&g_msgMapArg4, 0, 0);
    LogLine("[MSGMAP-REPLAY] invoking with a1=%p a2=%p a3=%p a4=%p", x1, x2, x3, x4);
    LONG64 rv = 0;
    __try { rv = g_origMsgMap(x1, x2, x3, x4); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[MSGMAP-REPLAY] SEH 0x%08lX", GetExceptionCode());
        return -8888;
    }
    LogLine("[MSGMAP-REPLAY] returned rv=0x%llX", (unsigned long long)rv);
    return rv;
}

// --- CMP variant: hook at EM_CMP.dll + 0x13920's enclosing function. ---

extern "C" __declspec(dllexport) int __cdecl CCInsp_HookCmpApply(void) {
    if (g_cmpApplyTarget != nullptr) {
        LogLine("[CMP-APPLY-HOOK] already installed at %p", g_cmpApplyTarget);
        return 0;
    }
    HMODULE cmp = GetModuleHandleW(L"EM_CMP.dll");
    if (!cmp) {
        LogLine("[CMP-APPLY-HOOK] EM_CMP.dll not loaded");
        return -1;
    }
    void* returnAddr = (BYTE*)cmp + 0x13920;
    void* fnStart = ResolveFunctionStart(returnAddr);
    if (!fnStart) {
        LogLine("[CMP-APPLY-HOOK] RtlLookupFunctionEntry returned null for EM_CMP+0x13920 (%p)", returnAddr);
        return -2;
    }
    LogLine("[CMP-APPLY-HOOK] target function start resolved: %p (EM_CMP+0x%llX)",
        fnStart, (unsigned long long)((BYTE*)fnStart - (BYTE*)cmp));

    void* tramp = nullptr;
    if (!InstallInlineHook(fnStart, (void*)&CmpApplyHook, &tramp)) {
        LogLine("[CMP-APPLY-HOOK] InstallInlineHook FAILED");
        return -3;
    }
    g_origCmpApply = (CmpApplyFn)tramp;
    g_cmpApplyTarget = fnStart;
    LogLine("[CMP-APPLY-HOOK] installed ok. trampoline=%p", tramp);
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl CCInsp_GetCmpApplyArgs(
    void** out1, void** out2, void** out3, void** out4)
{
    if (out1) *out1 = (void*)InterlockedCompareExchange64(&g_cmpApplyArg1, 0, 0);
    if (out2) *out2 = (void*)InterlockedCompareExchange64(&g_cmpApplyArg2, 0, 0);
    if (out3) *out3 = (void*)InterlockedCompareExchange64(&g_cmpApplyArg3, 0, 0);
    if (out4) *out4 = (void*)InterlockedCompareExchange64(&g_cmpApplyArg4, 0, 0);
    return (InterlockedCompareExchange(&g_cmpApplyHookCount, 0, 0) > 0) ? 1 : 0;
}

extern "C" __declspec(dllexport) LONG64 __cdecl CCInsp_ReplayCmpApply(
    void* overrideA1, void* overrideA2, void* overrideA3, void* overrideA4)
{
    if (!g_origCmpApply) {
        LogLine("[CMP-APPLY-REPLAY] no trampoline - hook not installed");
        return (LONG64)-9999;
    }
    void* a1 = overrideA1 ? overrideA1 : (void*)InterlockedCompareExchange64(&g_cmpApplyArg1, 0, 0);
    void* a2 = overrideA2 ? overrideA2 : (void*)InterlockedCompareExchange64(&g_cmpApplyArg2, 0, 0);
    void* a3 = overrideA3 ? overrideA3 : (void*)InterlockedCompareExchange64(&g_cmpApplyArg3, 0, 0);
    void* a4 = overrideA4 ? overrideA4 : (void*)InterlockedCompareExchange64(&g_cmpApplyArg4, 0, 0);
    LogLine("[CMP-APPLY-REPLAY] invoking with a1=%p a2=%p a3=%p a4=%p", a1, a2, a3, a4);
    LONG64 rv = 0;
    __try { rv = g_origCmpApply(a1, a2, a3, a4); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogLine("[CMP-APPLY-REPLAY] SEH 0x%08lX", GetExceptionCode());
        return (LONG64)-8888;
    }
    LogLine("[CMP-APPLY-REPLAY] returned rv=0x%llX", (unsigned long long)rv);
    return rv;
}

// Exported: dumps all three CC-state globals to the bridge log + returns a
// composite snapshot in a caller-allocated 96-byte buffer (three 8-byte
// pointers: [0]=FED.AS, [1]=FED.MS, [2]=ELC2.gbl_pxAs; rest zero).
extern "C" __declspec(dllexport) int __cdecl CCInsp_SnapshotState(void* outBuf) {
    if (!outBuf) return -1;
    void* fedAs = CCInsp_GetFEDataActionSummary();
    void* fedMs = CCInsp_GetFEDataModelSet();
    void* elc2As = CCInsp_GetELC2GlobalAs();
    void* capMs = (void*)InterlockedCompareExchange64(&g_lastCapturedModelSet, 0, 0);
    LogLine("[CC-INSP-SNAP] CERwinFEData.AS=%p CERwinFEData.MS=%p ELC2.gbl_pxAs=%p GA.capturedMs=%p",
        fedAs, fedMs, elc2As, capMs);
    void** out = (void**)outBuf;
    out[0] = fedAs;
    out[1] = fedMs;
    out[2] = elc2As;
    out[3] = capMs;
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
