/* Proxy test: loads build\version.dll (our module) by full path from a test exe and checks that
 *  - it exports exactly the 17 names of the system version.dll, with the same ordinals;
 *  - forwarded calls return the same as the system version.dll (size, data, query, language name);
 *  - the module imports nothing but KERNEL32 and USER32 (no other DLL, no network, no registry API);
 *  - in a process that is not acz.exe it does nothing (no log is written).
 *   proxytest.exe <path to our version.dll> */
#include <windows.h>
#include <stdio.h>
#include <string.h>

static int failed = 0, passed = 0;
#define CHECK(c, ...) do { if (c) passed++; else { failed++; printf("FAIL line %d: ", __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)

static const char *const names[17] = {
    "GetFileVersionInfoA", "GetFileVersionInfoByHandle", "GetFileVersionInfoExA", "GetFileVersionInfoExW",
    "GetFileVersionInfoSizeA", "GetFileVersionInfoSizeExA", "GetFileVersionInfoSizeExW", "GetFileVersionInfoSizeW",
    "GetFileVersionInfoW", "VerFindFileA", "VerFindFileW", "VerInstallFileA", "VerInstallFileW",
    "VerLanguageNameA", "VerLanguageNameW", "VerQueryValueA", "VerQueryValueW",
};

typedef DWORD (WINAPI *T_SIZE)(LPCWSTR, LPDWORD);
typedef BOOL (WINAPI *T_INFO)(LPCWSTR, DWORD, DWORD, LPVOID);
typedef BOOL (WINAPI *T_QUERY)(LPCVOID, LPCWSTR, LPVOID *, PUINT);
typedef DWORD (WINAPI *T_LANG)(DWORD, LPWSTR, DWORD);

/* names of the DLLs our file imports (own PE parse of the file) */
static void check_imports(const WCHAR *path)
{
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    static unsigned char b[1 << 20];
    DWORD got = 0, lf, nsec, so, irva, i;
    int other = 0, n = 0;
    ReadFile(f, b, sizeof b, &got, NULL);
    CloseHandle(f);
    lf = *(DWORD *)(b + 0x3c);
    nsec = *(WORD *)(b + lf + 6);
    so = lf + 24 + *(WORD *)(b + lf + 20);
    irva = *(DWORD *)(b + lf + 24 + 112 + 8);
    for (i = 0;; i++) {
        DWORD e = 0, k, name;
        for (k = 0; k < nsec; k++) {
            DWORD va = *(DWORD *)(b + so + 40 * k + 12), vs = *(DWORD *)(b + so + 40 * k + 8), rp = *(DWORD *)(b + so + 40 * k + 20);
            if (irva + 20 * i >= va && irva + 20 * i < va + vs) e = irva + 20 * i - va + rp;
        }
        if (!e || e + 20 > got) break;
        name = *(DWORD *)(b + e + 12);
        if (!name) break;
        for (k = 0; k < nsec; k++) {
            DWORD va = *(DWORD *)(b + so + 40 * k + 12), vs = *(DWORD *)(b + so + 40 * k + 8), rp = *(DWORD *)(b + so + 40 * k + 20);
            if (name >= va && name < va + vs) {
                const char *s = (const char *)b + name - va + rp;
                printf("imports %s\n", s);
                n++;
                if (_stricmp(s, "KERNEL32.dll") != 0 && _stricmp(s, "USER32.dll") != 0) other++;
            }
        }
    }
    CHECK(n >= 1 && other == 0, "imports: %d dlls, %d not kernel32/user32", n, other);
}

int main(int argc, char **argv)
{
    WCHAR arg[MAX_PATH], ours[MAX_PATH], sys[MAX_PATH], target[MAX_PATH], buf[128], buf2[128], logp[MAX_PATH];
    HMODULE m, s;
    int i;
    DWORD h1 = 0, h2 = 0, n1, n2;
    FILETIME before = { 0 };
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (argc < 2) { printf("usage: proxytest <our version.dll>\n"); return 2; }
    MultiByteToWideChar(CP_ACP, 0, argv[1], -1, arg, MAX_PATH);
    GetFullPathNameW(arg, MAX_PATH, ours, NULL);
    GetSystemDirectoryW(sys, MAX_PATH);
    lstrcpyW(target, sys); lstrcatW(target, L"\\kernel32.dll");
    lstrcatW(sys, L"\\version.dll");
    GetEnvironmentVariableW(L"LOCALAPPDATA", logp, MAX_PATH);
    lstrcatW(logp, L"\\ACZRuLauncher\\module.log");
    if (GetFileAttributesExW(logp, GetFileExInfoStandard, &fa)) before = fa.ftLastWriteTime;

    check_imports(ours);

    m = LoadLibraryW(ours);
    CHECK(m != NULL, "LoadLibrary(our dll) error %lu", GetLastError());
    s = LoadLibraryW(sys);
    CHECK(s != NULL && s != m, "system version.dll must be a different module");
    if (!m || !s) goto end;

    for (i = 0; i < 17; i++) {
        FARPROC byname = GetProcAddress(m, names[i]), byord = GetProcAddress(m, MAKEINTRESOURCEA(i + 1));
        CHECK(byname != NULL && byname == byord, "%s: by name %p, by ordinal %d %p", names[i], (void *)byname, i + 1, (void *)byord);
    }
    CHECK(GetProcAddress(m, MAKEINTRESOURCEA(18)) == NULL, "export #18 must not exist");

    n1 = ((T_SIZE)GetProcAddress(m, "GetFileVersionInfoSizeW"))(target, &h1);
    n2 = ((T_SIZE)GetProcAddress(s, "GetFileVersionInfoSizeW"))(target, &h2);
    CHECK(n1 > 0 && n1 == n2, "GetFileVersionInfoSizeW %lu vs %lu", n1, n2);
    if (n1 > 0 && n1 == n2) {
        BYTE *a = (BYTE *)calloc(n1, 1), *b = (BYTE *)calloc(n2, 1);   /* zeroed: the API leaves padding untouched */
        LPVOID va = NULL, vb = NULL;
        UINT la = 0, lb = 0;
        CHECK(((T_INFO)GetProcAddress(m, "GetFileVersionInfoW"))(target, 0, n1, a), "GetFileVersionInfoW (ours)");
        CHECK(((T_INFO)GetProcAddress(s, "GetFileVersionInfoW"))(target, 0, n2, b), "GetFileVersionInfoW (system)");
        CHECK(memcmp(a, b, n1) == 0, "version data differs");
        CHECK(((T_QUERY)GetProcAddress(m, "VerQueryValueW"))(a, L"\\", &va, &la) &&
              ((T_QUERY)GetProcAddress(s, "VerQueryValueW"))(b, L"\\", &vb, &lb) && la == lb && la > 0 &&
              memcmp(va, vb, la) == 0, "VerQueryValueW(\\)");
        free(a); free(b);
    }
    n1 = ((T_LANG)GetProcAddress(m, "VerLanguageNameW"))(0x0419, buf, 128);
    n2 = ((T_LANG)GetProcAddress(s, "VerLanguageNameW"))(0x0419, buf2, 128);
    CHECK(n1 > 0 && n1 == n2 && lstrcmpW(buf, buf2) == 0, "VerLanguageNameW");

    Sleep(1500);   /* the worker thread starts and must quit at once: host is not acz.exe */
    if (GetFileAttributesExW(logp, GetFileExInfoStandard, &fa))
        CHECK(CompareFileTime(&fa.ftLastWriteTime, &before) == 0, "module.log was written by a non-game process");
    else passed++;
    FreeLibrary(m);
end:
    printf("%d passed, %d failed\n%s\n", passed, failed, failed ? "RESULT FAILED" : "RESULT OK");
    return failed ? 1 : 0;
}
