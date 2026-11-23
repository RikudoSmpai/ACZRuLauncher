/* version.dll proxy: all 17 exports of the system version.dll, each forwarded to the real one.
 * The real DLL is loaded on the first call, only by the full path from GetSystemDirectoryW
 * (never by name: that would find this file again). Nothing else is loaded. */
#include <windows.h>

enum {
    F_GetFileVersionInfoA, F_GetFileVersionInfoByHandle, F_GetFileVersionInfoExA, F_GetFileVersionInfoExW,
    F_GetFileVersionInfoSizeA, F_GetFileVersionInfoSizeExA, F_GetFileVersionInfoSizeExW, F_GetFileVersionInfoSizeW,
    F_GetFileVersionInfoW, F_VerFindFileA, F_VerFindFileW, F_VerInstallFileA, F_VerInstallFileW,
    F_VerLanguageNameA, F_VerLanguageNameW, F_VerQueryValueA, F_VerQueryValueW, F_COUNT
};

static const char *const names[F_COUNT] = {
    "GetFileVersionInfoA", "GetFileVersionInfoByHandle", "GetFileVersionInfoExA", "GetFileVersionInfoExW",
    "GetFileVersionInfoSizeA", "GetFileVersionInfoSizeExA", "GetFileVersionInfoSizeExW", "GetFileVersionInfoSizeW",
    "GetFileVersionInfoW", "VerFindFileA", "VerFindFileW", "VerInstallFileA", "VerInstallFileW",
    "VerLanguageNameA", "VerLanguageNameW", "VerQueryValueA", "VerQueryValueW",
};

static FARPROC real[F_COUNT];
static INIT_ONCE once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK load_real(PINIT_ONCE o, PVOID param, PVOID *ctx)
{
    WCHAR path[MAX_PATH + 32];
    UINT n = GetSystemDirectoryW(path, MAX_PATH);
    HMODULE h;
    int i;
    (void)o; (void)param; (void)ctx;
    if (n == 0 || n >= MAX_PATH) return TRUE;
    lstrcpyW(path + n, L"\\version.dll");
    h = LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!h) return TRUE;
    for (i = 0; i < F_COUNT; i++) real[i] = GetProcAddress(h, names[i]);
    return TRUE;
}

static FARPROC get(int i)
{
    InitOnceExecuteOnce(&once, load_real, NULL, NULL);
    if (!real[i]) SetLastError(ERROR_PROC_NOT_FOUND);
    return real[i];
}

#define CALL(T, i, fail, args) do { T f_ = (T)get(i); return f_ ? f_ args : (fail); } while (0)

typedef BOOL (WINAPI *T_GFVI_A)(LPCSTR, DWORD, DWORD, LPVOID);
typedef BOOL (WINAPI *T_GFVI_W)(LPCWSTR, DWORD, DWORD, LPVOID);
typedef BOOL (WINAPI *T_GFVIEX_A)(DWORD, LPCSTR, DWORD, DWORD, LPVOID);
typedef BOOL (WINAPI *T_GFVIEX_W)(DWORD, LPCWSTR, DWORD, DWORD, LPVOID);
typedef DWORD (WINAPI *T_GFVIS_A)(LPCSTR, LPDWORD);
typedef DWORD (WINAPI *T_GFVIS_W)(LPCWSTR, LPDWORD);
typedef DWORD (WINAPI *T_GFVISEX_A)(DWORD, LPCSTR, LPDWORD);
typedef DWORD (WINAPI *T_GFVISEX_W)(DWORD, LPCWSTR, LPDWORD);
typedef DWORD (WINAPI *T_VFF_A)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, LPSTR, PUINT);
typedef DWORD (WINAPI *T_VFF_W)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, LPWSTR, PUINT);
typedef DWORD (WINAPI *T_VIF_A)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT);
typedef DWORD (WINAPI *T_VIF_W)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT);
typedef DWORD (WINAPI *T_VLN_A)(DWORD, LPSTR, DWORD);
typedef DWORD (WINAPI *T_VLN_W)(DWORD, LPWSTR, DWORD);
typedef BOOL (WINAPI *T_VQV_A)(LPCVOID, LPCSTR, LPVOID *, PUINT);
typedef BOOL (WINAPI *T_VQV_W)(LPCVOID, LPCWSTR, LPVOID *, PUINT);
/* undocumented: integer/pointer arguments only. Eight are passed through: on x64 extra arguments are harmless
 * (registers and the caller's stack are only read), so the exact count does not matter. */
typedef INT_PTR (WINAPI *T_GFVIBH)(INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR, INT_PTR);

BOOL WINAPI P_GetFileVersionInfoA(LPCSTR a, DWORD b, DWORD c, LPVOID d) { CALL(T_GFVI_A, F_GetFileVersionInfoA, FALSE, (a, b, c, d)); }
INT_PTR WINAPI P_GetFileVersionInfoByHandle(INT_PTR a, INT_PTR b, INT_PTR c, INT_PTR d, INT_PTR e, INT_PTR f, INT_PTR g, INT_PTR h) { CALL(T_GFVIBH, F_GetFileVersionInfoByHandle, 0, (a, b, c, d, e, f, g, h)); }
BOOL WINAPI P_GetFileVersionInfoExA(DWORD a, LPCSTR b, DWORD c, DWORD d, LPVOID e) { CALL(T_GFVIEX_A, F_GetFileVersionInfoExA, FALSE, (a, b, c, d, e)); }
BOOL WINAPI P_GetFileVersionInfoExW(DWORD a, LPCWSTR b, DWORD c, DWORD d, LPVOID e) { CALL(T_GFVIEX_W, F_GetFileVersionInfoExW, FALSE, (a, b, c, d, e)); }
DWORD WINAPI P_GetFileVersionInfoSizeA(LPCSTR a, LPDWORD b) { CALL(T_GFVIS_A, F_GetFileVersionInfoSizeA, 0, (a, b)); }
DWORD WINAPI P_GetFileVersionInfoSizeExA(DWORD a, LPCSTR b, LPDWORD c) { CALL(T_GFVISEX_A, F_GetFileVersionInfoSizeExA, 0, (a, b, c)); }
DWORD WINAPI P_GetFileVersionInfoSizeExW(DWORD a, LPCWSTR b, LPDWORD c) { CALL(T_GFVISEX_W, F_GetFileVersionInfoSizeExW, 0, (a, b, c)); }
DWORD WINAPI P_GetFileVersionInfoSizeW(LPCWSTR a, LPDWORD b) { CALL(T_GFVIS_W, F_GetFileVersionInfoSizeW, 0, (a, b)); }
BOOL WINAPI P_GetFileVersionInfoW(LPCWSTR a, DWORD b, DWORD c, LPVOID d) { CALL(T_GFVI_W, F_GetFileVersionInfoW, FALSE, (a, b, c, d)); }
DWORD WINAPI P_VerFindFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT f, LPSTR g, PUINT h) { CALL(T_VFF_A, F_VerFindFileA, 0, (a, b, c, d, e, f, g, h)); }
DWORD WINAPI P_VerFindFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT f, LPWSTR g, PUINT h) { CALL(T_VFF_W, F_VerFindFileW, 0, (a, b, c, d, e, f, g, h)); }
DWORD WINAPI P_VerInstallFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPCSTR f, LPSTR g, PUINT h) { CALL(T_VIF_A, F_VerInstallFileA, VIF_CANNOTREADSRC, (a, b, c, d, e, f, g, h)); }
DWORD WINAPI P_VerInstallFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPCWSTR f, LPWSTR g, PUINT h) { CALL(T_VIF_W, F_VerInstallFileW, VIF_CANNOTREADSRC, (a, b, c, d, e, f, g, h)); }
DWORD WINAPI P_VerLanguageNameA(DWORD a, LPSTR b, DWORD c) { CALL(T_VLN_A, F_VerLanguageNameA, 0, (a, b, c)); }
DWORD WINAPI P_VerLanguageNameW(DWORD a, LPWSTR b, DWORD c) { CALL(T_VLN_W, F_VerLanguageNameW, 0, (a, b, c)); }
BOOL WINAPI P_VerQueryValueA(LPCVOID a, LPCSTR b, LPVOID *c, PUINT d) { CALL(T_VQV_A, F_VerQueryValueA, FALSE, (a, b, c, d)); }
BOOL WINAPI P_VerQueryValueW(LPCVOID a, LPCWSTR b, LPVOID *c, PUINT d) { CALL(T_VQV_W, F_VerQueryValueW, FALSE, (a, b, c, d)); }
