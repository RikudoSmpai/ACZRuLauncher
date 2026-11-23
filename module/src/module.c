/* ACZ Russian text module (version.dll proxy).
 *
 * What it does, in this order, in ONE background thread:
 *   1. does nothing at all unless the host process is acz.exe;
 *   2. waits until the game window exists (the game is fully started; timeout 5 min -> nothing changed);
 *   3. does nothing if an anti-cheat is found next to the game (EasyAntiCheat, BattlEye, ...);
 *   4. does nothing unless settings\setting.ini says SystemLanguage=UK (the slot of the Russian translation);
 *   5. checks the PE header of acz.exe and ONE sha256 over all string slots of the table in .rdata, every
 *      group, enabled or not (all or nothing);
 *   6. for each string of an enabled group: page protection -> writable, copy the Russian bytes, protection back;
 *   7. writes a short log (%LOCALAPPDATA%\ACZRuLauncher\module.log, no paths, no personal data) and exits.
 * It never touches .text/.bind, code, imports, the entry point or anything of Steam; it loads no DLL except
 * the real System32\version.dll (proxy.c), opens no network, starts no process and writes no registry. */
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include "patch.h"
#include "patches_gen.h"

#ifndef ACZ_MODULE_VERSION
#define ACZ_MODULE_VERSION "1.0.0"
#endif

#define WINDOW_TIMEOUT_MS (5 * 60 * 1000)
#define SETTLE_MS 3000

static HANDLE g_log = INVALID_HANDLE_VALUE;

static void log_open(void)
{
    WCHAR dir[MAX_PATH], path[MAX_PATH + 64];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 40) return;
    lstrcpyW(path, dir);
    lstrcatW(path, L"\\ACZRuLauncher");
    CreateDirectoryW(path, NULL);
    lstrcatW(path, L"\\module.log");
    g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

static void mlog(const char *fmt, ...)
{
    char buf[512];
    SYSTEMTIME t;
    int k;
    va_list ap;
    DWORD w;
    if (g_log == INVALID_HANDLE_VALUE) return;
    GetLocalTime(&t);
    k = snprintf(buf, sizeof buf, "%04d-%02d-%02d %02d:%02d:%02d ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    va_start(ap, fmt);
    k += vsnprintf(buf + k, sizeof buf - k - 2, fmt, ap);
    va_end(ap);
    if (k > (int)sizeof buf - 2) k = (int)sizeof buf - 2;
    buf[k++] = '\r'; buf[k++] = '\n';
    WriteFile(g_log, buf, (DWORD)k, &w, NULL);
}

/* folder of acz.exe into dir (with trailing backslash); 1 if the host is acz.exe */
static int host_is_game(WCHAR *dir, size_t cap)
{
    WCHAR *name;
    DWORD n = GetModuleFileNameW(NULL, dir, (DWORD)cap);
    if (n == 0 || n >= cap) return 0;
    name = dir + n;
    while (name > dir && name[-1] != L'\\' && name[-1] != L'/') name--;
    if (lstrcmpiW(name, L"acz.exe") != 0) return 0;
    *name = 0;
    return 1;
}

static BOOL CALLBACK find_game_window(HWND h, LPARAM lp)
{
    DWORD pid = 0;
    WCHAR title[64];
    GetWindowThreadProcessId(h, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(h)) return TRUE;
    if (GetWindowTextW(h, title, 64) > 0 && wcsncmp(title, L"ACE COMBAT", 10) == 0) { *(int *)lp = 1; return FALSE; }
    return TRUE;
}

static int wait_game_window(void)
{
    DWORD t0 = GetTickCount();
    for (;;) {
        int found = 0;
        EnumWindows(find_game_window, (LPARAM)&found);
        if (found) return 1;
        if (GetTickCount() - t0 > WINDOW_TIMEOUT_MS) return 0;
        Sleep(500);
    }
}

static int exists(const WCHAR *dir, const WCHAR *pattern)
{
    WCHAR p[MAX_PATH + 64];
    WIN32_FIND_DATAW fd;
    HANDLE h;
    if (lstrlenW(dir) + lstrlenW(pattern) >= MAX_PATH + 60) return 0;
    lstrcpyW(p, dir);
    lstrcatW(p, pattern);
    h = FindFirstFileW(p, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    FindClose(h);
    return 1;
}

/* anti-cheat next to the game or one folder up (Zero inside ACE COMBAT 8 lives in "ACE COMBAT 8\ACZero") */
static const WCHAR *anticheat(const WCHAR *gamedir)
{
    static const WCHAR *const pats[] = { L"EasyAntiCheat*", L"start_protected_game*", L"BattlEye*", L"BEService*",
                                         L"vgk*", L"nProtect*", L"GameGuard*", L"xigncode*", L"EOSBootstrapper*" };
    WCHAR up[MAX_PATH + 8];
    int i, j;
    lstrcpyW(up, gamedir);
    j = lstrlenW(up);
    if (j > 0) up[--j] = 0;                       /* drop trailing backslash */
    while (j > 0 && up[j - 1] != L'\\') up[--j] = 0;  /* keep "...\parent\" */
    for (i = 0; i < (int)(sizeof pats / sizeof pats[0]); i++)
        if (exists(gamedir, pats[i]) || (j > 3 && exists(up, pats[i]))) return pats[i];
    return NULL;
}

/* SystemLanguage value from settings\setting.ini (same rule as the launcher: first "SystemLanguage=" line) */
static int read_language(const WCHAR *gamedir, char *val, size_t cap)
{
    WCHAR p[MAX_PATH + 64];
    char buf[16384];
    DWORD got = 0;
    HANDLE h;
    char *s, *e, *line;
    lstrcpyW(p, gamedir);
    lstrcatW(p, L"settings\\setting.ini");
    h = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    ReadFile(h, buf, sizeof buf - 1, &got, NULL);
    CloseHandle(h);
    buf[got] = 0;
    for (line = buf; line && *line; line = (e = strchr(line, '\n')) ? e + 1 : NULL) {
        s = line;
        while (*s == ' ' || *s == '\t') s++;
        if (_strnicmp(s, "SystemLanguage", 14) != 0) continue;
        s += 14;
        while (*s == ' ' || *s == '\t') s++;
        if (*s != '=') continue;
        s++;
        while (*s == ' ') s++;
        e = s;
        while (*e && *e != '\t' && *e != ' ' && *e != ';' && *e != '\r' && *e != '\n') e++;
        if ((size_t)(e - s) >= cap) return 0;
        memcpy(val, s, e - s);
        val[e - s] = 0;
        return 1;
    }
    return 0;
}

static int mem_unprotect(void *ctx, uint8_t *p, size_t n, uint32_t *old)
{
    DWORD o = 0;
    MEMORY_BASIC_INFORMATION mi;
    (void)ctx;
    /* our pages must be committed, read-only image pages (.rdata) */
    if (!VirtualQuery(p, &mi, sizeof mi) || mi.State != MEM_COMMIT || mi.Type != MEM_IMAGE || mi.Protect != PAGE_READONLY) return 0;
    if (!VirtualProtect(p, n, PAGE_READWRITE, &o)) return 0;
    *old = o;
    return 1;
}

static int mem_protect(void *ctx, uint8_t *p, size_t n, uint32_t old)
{
    DWORD o = 0;
    (void)ctx;
    return VirtualProtect(p, n, old, &o) != 0;
}

static DWORD WINAPI worker(LPVOID arg)
{
    WCHAR dir[MAX_PATH];
    char lang[16], why[200];
    const WCHAR *ac;
    const uint8_t *base;
    acz_expect ex = { ACZ_EXE_TIMESTAMP, ACZ_EXE_SIZE_OF_IMAGE, ACZ_RDATA_RVA, ACZ_RDATA_VSIZE };
    acz_mem_ops ops = { mem_unprotect, mem_protect, NULL };
    int applied = 0, i;
    (void)arg;

    if (!host_is_game(dir, MAX_PATH)) return 0;          /* not the game: no log, nothing */
    log_open();
    mlog("ACZ RU module %s%s: start, %d strings in table", ACZ_MODULE_VERSION, ACZ_PROBE_BUILD ? " (PROBE build)" : "", ACZ_PATCH_COUNT);
    for (i = 0; i < ACZ_GROUP_COUNT; i++)
        mlog("group %s: %s", acz_group_names[i], acz_group_enabled[i] ? "on" : "off");

    if (!wait_game_window()) { mlog("no game window within %d s - nothing changed", WINDOW_TIMEOUT_MS / 1000); goto out; }
    Sleep(SETTLE_MS);
    mlog("game window found");

    if ((ac = anticheat(dir)) != NULL) { mlog("anti-cheat found (%ls) - nothing changed", ac); goto out; }
    if (!read_language(dir, lang, sizeof lang)) { mlog("settings\\setting.ini: no SystemLanguage - nothing changed"); goto out; }
    if (lstrcmpiA(lang, "UK") != 0) { mlog("SystemLanguage=%s, not UK - nothing changed (English stays English)", lang); goto out; }

    base = (const uint8_t *)GetModuleHandleW(NULL);
    if (!acz_check_header(base, 0x1000, &ex, why, sizeof why)) { mlog("exe header: %s - nothing changed", why); goto out; }
    if (!acz_verify_all(base, &ex, acz_patches, ACZ_PATCH_COUNT, ACZ_GROUP_COUNT, acz_table_sha256, acz_group_enabled, why, sizeof why)) {
        mlog("check: %s - nothing changed", why);
        goto out;
    }
    mlog("check: sha256 of the table (%d strings, %d groups) matches", ACZ_PATCH_COUNT, ACZ_GROUP_COUNT);
    if (!acz_apply((uint8_t *)base, acz_patches, ACZ_PATCH_COUNT, acz_group_enabled, &ops, &applied, why, sizeof why)) {
        mlog("write: %s - rolled back, nothing changed", why);
        goto out;
    }
    mlog("done: %d strings replaced", applied);
out:
    if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log);
    g_log = INVALID_HANDLE_VALUE;
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        HANDLE t;
        DisableThreadLibraryCalls(inst);
        t = CreateThread(NULL, 0, worker, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
