/* Offline test of the patch table, without running anything of the game.
 *
 *   patchtest.exe                 synthetic tests only (sha256 vectors, header checks, write rule, rollback)
 *   patchtest.exe <acz.exe>       + the real table on an image of acz.exe laid out from the file
 *
 * The image is built from the PE headers and the raw bytes of .rdata ONLY (the file is read at those two
 * places, nothing else: .text and .bind are never read). Then the same code as in the module checks the
 * header and the one sha256 of the table, applies it (release groups, then all groups = probe build)
 * and the test compares the image byte for byte: only bytes [rva, rva+L+1) of enabled strings may differ,
 * and every write request covers exactly that object. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../src/patch.h"
#include "../src/sha256.h"
#include "../src/patches_gen.h"

static int failed = 0, passed = 0;
#define CHECK(c, ...) do { if (c) passed++; else { failed++; printf("FAIL %s:%d ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)

/* memory ops that record every range made writable */
typedef struct rec_ctx { uint8_t *base; int n; uint32_t off[512], len[512]; int fail_at; } rec_ctx;
static int rec_unprotect(void *ctx, uint8_t *p, size_t n, uint32_t *old)
{
    rec_ctx *r = (rec_ctx *)ctx;
    *old = 2;
    if (r->n < 512) { r->off[r->n] = (uint32_t)(p - r->base); r->len[r->n] = (uint32_t)n; }
    r->n++;
    return r->fail_at == 0 || r->n != r->fail_at;
}
static int ok_protect(void *ctx, uint8_t *p, size_t n, uint32_t old) { (void)ctx; (void)p; (void)n; return old == 2; }

static void hex(const uint8_t *h, char *out) { int i; for (i = 0; i < 32; i++) sprintf(out + 2 * i, "%02x", h[i]); }

static size_t text_len(const acz_patch *p) { size_t k = 0; while (k < (size_t)p->len + 1 && p->new_bytes[k]) k++; return k; }

static void test_sha256(void)
{
    uint8_t h[32], h2[32];
    char s[65];
    static char big[1000001];
    size_t cut;
    acz_sha256((const uint8_t *)"", 0, h); hex(h, s);
    CHECK(strcmp(s, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") == 0, "sha256('') %s", s);
    acz_sha256((const uint8_t *)"abc", 3, h); hex(h, s);
    CHECK(strcmp(s, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad") == 0, "sha256(abc) %s", s);
    acz_sha256((const uint8_t *)"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", 56, h); hex(h, s);
    CHECK(strcmp(s, "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1") == 0, "sha256(448 bit) %s", s);
    memset(big, 'a', 1000000);
    acz_sha256((const uint8_t *)big, 1000000, h); hex(h, s);
    CHECK(strcmp(s, "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0") == 0, "sha256(1M a) %s", s);
    /* streaming in two pieces at every cut = one call */
    for (cut = 0; cut <= 200; cut++) {
        acz_sha256_ctx c;
        acz_sha256((const uint8_t *)big, 200, h);
        acz_sha256_init(&c);
        acz_sha256_update(&c, (const uint8_t *)big, cut);
        acz_sha256_update(&c, (const uint8_t *)big + cut, 200 - cut);
        acz_sha256_final(&c, h2);
        if (memcmp(h, h2, 32) != 0) { CHECK(0, "streaming sha256 differs at cut %zu", cut); break; }
    }
    CHECK(cut > 200, "streaming sha256");
}

/* a tiny fake x64 image with .rdata where the table expects it, and one synthetic patch table */
static void test_synthetic(void)
{
    static uint8_t img[0x3000];
    uint8_t before[sizeof img];
    acz_expect ex = { 0x11223344u, 0x3000u, 0x1000u, 0x2000u };
    acz_patch t[3];
    uint8_t th[32];
    static const uint8_t n0[5] = { 'a', 'b', 0, 0, 0 }, n1[2] = { 'c', 'd' }, n2[5] = { 'd', 'd', 'd', 'd', 'd' };
    static const uint8_t bad_after_nul[5] = { 'a', 0, 'b', 0, 0 };
    const int on[2] = { 1, 1 }, only0[2] = { 1, 0 };
    rec_ctx rc;
    acz_mem_ops ops = { rec_unprotect, ok_protect, &rc };
    int applied, i, outside = 0;
    char why[200];
    uint8_t *pe;

    memset(img, 0, sizeof img);
    img[0] = 'M'; img[1] = 'Z'; img[0x3c] = 0x80;
    pe = img + 0x80;
    memcpy(pe, "PE\0\0", 4);
    pe[4] = 0x64; pe[5] = 0x86; pe[6] = 1;                              /* machine x64, 1 section */
    memcpy(pe + 8, &ex.timestamp, 4);
    pe[20] = 0xf0;                                                     /* SizeOfOptionalHeader */
    pe[24] = 0x0b; pe[25] = 0x02;                                      /* PE32+ */
    memcpy(pe + 24 + 56, &ex.size_of_image, 4);
    memcpy(pe + 24 + 0xf0, ".rdata\0\0", 8);
    memcpy(pe + 24 + 0xf0 + 8, &ex.rdata_vsize, 4);
    memcpy(pe + 24 + 0xf0 + 12, &ex.rdata_rva, 4);
    memcpy(img + 0x1100, "XYZW\0\0\0\0", 8);
    memcpy(img + 0x1108, "Q\0\0\0\0\0\0\0", 8);
    memcpy(img + 0x1200, "LONG\0\0\0\0", 8);
    t[0].rva = 0x1100; t[0].len = 4; t[0].slot = 8; t[0].group = 0; t[0].new_bytes = n0;   /* shorter text */
    t[1].rva = 0x1108; t[1].len = 1; t[1].slot = 8; t[1].group = 1; t[1].new_bytes = n1;   /* L+1 text */
    t[2].rva = 0x1200; t[2].len = 4; t[2].slot = 8; t[2].group = 0; t[2].new_bytes = n2;   /* L+1 text */
    acz_table_hash(img, t, 3, th);

    CHECK(acz_check_header(img, sizeof img, &ex, why, sizeof why), "header: %s", why);
    { acz_expect bad = ex; bad.timestamp ^= 1; CHECK(!acz_check_header(img, sizeof img, &bad, why, sizeof why), "other timestamp accepted"); }
    { acz_expect bad = ex; bad.rdata_vsize += 0x10; CHECK(!acz_check_header(img, sizeof img, &bad, why, sizeof why), "other .rdata accepted"); }
    CHECK(acz_verify_all(img, &ex, t, 3, 2, th, only0, why, sizeof why), "verify: %s", why);

    /* the table hash covers rva and slot too, not only the bytes */
    { acz_patch o[3]; uint8_t h[32]; memcpy(o, t, sizeof o); o[2].slot = 7; acz_table_hash(img, o, 3, h);
      CHECK(memcmp(h, th, 32) != 0, "slot length not in the table hash"); }
    { acz_patch o[3]; uint8_t h[32]; memcpy(o, t, sizeof o); o[0].rva += 0x10; acz_table_hash(img, o, 3, h);
      CHECK(memcmp(h, th, 32) != 0, "rva not in the table hash"); }
    /* ... and every string of every group: the table without the string of the disabled group 1 is refused */
    { acz_patch o[2]; o[0] = t[0]; o[1] = t[2];
      CHECK(!acz_verify_all(img, &ex, o, 2, 2, th, only0, why, sizeof why) && strstr(why, "sha256 mismatch"), "table without a disabled string accepted (%s)", why); }

    /* one changed byte anywhere in any slot (also in the zero tail) -> refuse */
    for (i = 0; i < 3; i++) {
        int j;
        for (j = 0; j < t[i].slot; j++) {
            img[t[i].rva + j] ^= 0x41;
            CHECK(!acz_verify_all(img, &ex, t, 3, 2, th, only0, why, sizeof why) && strstr(why, "sha256 mismatch"),
                  "patch %d byte %d change not caught (%s)", i, j, why);
            img[t[i].rva + j] ^= 0x41;
        }
    }
    /* slot outside .rdata / overlapping -> refuse */
    { acz_patch o = t[0]; o.rva = 0x0ff8; CHECK(!acz_verify_all(img, &ex, &o, 1, 2, th, on, why, sizeof why) && strstr(why, "outside .rdata"), "slot outside .rdata accepted (%s)", why); }
    { acz_patch o[2]; o[0] = t[0]; o[1] = t[0]; o[1].rva += 4; CHECK(!acz_verify_all(img, &ex, o, 2, 2, th, on, why, sizeof why) && strstr(why, "overlaps"), "overlap accepted (%s)", why); }
    /* write rule: an L+1 text needs a zero after the object inside the slot; nothing after the NUL */
    { acz_patch o = t[2]; o.slot = 5; CHECK(!acz_verify_all(img, &ex, &o, 1, 2, th, on, why, sizeof why) && strstr(why, "L+1"), "L+1 text without a following zero accepted (%s)", why); }
    { acz_patch o = t[0]; o.new_bytes = bad_after_nul; CHECK(!acz_verify_all(img, &ex, &o, 1, 2, th, on, why, sizeof why) && strstr(why, "after the NUL"), "bytes after the NUL accepted (%s)", why); }
    { acz_patch o = t[0]; o.group = 5; CHECK(!acz_verify_all(img, &ex, &o, 1, 2, th, on, why, sizeof why) && strstr(why, "unknown group"), "unknown group accepted (%s)", why); }

    /* group 1 off: only objects 0 and 2 change, write requests = exactly [rva, rva+L+1) */
    memcpy(before, img, sizeof img);
    memset(&rc, 0, sizeof rc); rc.base = img;
    CHECK(acz_apply(img, t, 3, only0, &ops, &applied, why, sizeof why) && applied == 2, "apply: %s (%d)", why, applied);
    CHECK(rc.n == 2 && rc.off[0] == 0x1100 && rc.len[0] == 5 && rc.off[1] == 0x1200 && rc.len[1] == 5, "write requests (%d)", rc.n);
    for (i = 0; i < (int)sizeof img; i++)
        if (img[i] != before[i] && !((i >= 0x1100 && i < 0x1105) || (i >= 0x1200 && i < 0x1205))) outside++;
    CHECK(outside == 0, "%d bytes changed outside [0, L+1) of enabled strings", outside);
    CHECK(memcmp(img + 0x1100, n0, 5) == 0 && memcmp(img + 0x1200, n2, 5) == 0 && img[0x1205] == 0 && memcmp(img + 0x1108, before + 0x1108, 8) == 0, "contents");
    CHECK(!acz_verify_all(img, &ex, t, 3, 2, th, only0, why, sizeof why) && strstr(why, "already applied"), "second run not refused (%s)", why);

    /* all groups: the L+1 text of patch 1 writes 2 bytes, its terminating zero (0x110a) is not written */
    memcpy(img, before, sizeof img);
    memset(&rc, 0, sizeof rc); rc.base = img;
    CHECK(acz_apply(img, t, 3, on, &ops, &applied, why, sizeof why) && applied == 3, "apply all: %s", why);
    CHECK(rc.n == 3 && rc.off[1] == 0x1108 && rc.len[1] == 2, "write request of the L+1 text");

    /* failure on the 3rd unprotect -> everything written so far rolled back */
    memcpy(img, before, sizeof img);
    memset(&rc, 0, sizeof rc); rc.base = img; rc.fail_at = 3;
    CHECK(!acz_apply(img, t, 3, on, &ops, &applied, why, sizeof why), "apply should fail");
    CHECK(memcmp(img, before, sizeof img) == 0, "rollback left changes");
}

static uint8_t *read_at(FILE *f, long off, size_t n)
{
    uint8_t *b = (uint8_t *)malloc(n ? n : 1);
    if (!b || fseek(f, off, SEEK_SET) != 0 || fread(b, 1, n, f) != n) { free(b); return NULL; }
    return b;
}

static uint32_t rd32(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }

/* the table itself: every text <= L+1, the ones longer than the original are listed */
static void test_table(void)
{
    int k, longer = 0, too_long = 0;
    for (k = 0; k < ACZ_PATCH_COUNT; k++) {
        const acz_patch *p = &acz_patches[k];
        size_t n = text_len(p);
        if (n > p->len) {
            longer++;
            printf("  longer than the original: rva %x, L %u, text %zu (L+1 = %u), slot %u\n", p->rva, p->len, n, p->len + 1u, p->slot);
        }
        if (n > (size_t)p->len + 1 || (n == (size_t)p->len + 1 && p->slot < p->len + 2)) too_long++;
    }
    CHECK(too_long == 0, "%d texts do not fit [0, L+1)", too_long);
    CHECK(longer == 5, "%d texts longer than the original, expected 5", longer);
}

static void test_real(const char *path, int probe)
{
    FILE *f = fopen(path, "rb");
    uint8_t *hdr, *img, *before, *raw;
    uint32_t lfanew, nsec, so, i, rawptr = 0, rawsize = 0, va = 0, vs = 0;
    acz_expect ex = { ACZ_EXE_TIMESTAMP, ACZ_EXE_SIZE_OF_IMAGE, ACZ_RDATA_RVA, ACZ_RDATA_VSIZE };
    static rec_ctx rc;
    acz_mem_ops ops = { rec_unprotect, ok_protect, &rc };
    int enabled[8] = { 0 }, applied = 0, expect = 0, g, k, flips = 0, caught = 0, reqbad = 0;
    size_t outside = 0, j;
    char why[200];
    if (!f) { CHECK(0, "cannot open %s", path); return; }
    hdr = read_at(f, 0, 0x1000);                                   /* PE headers */
    if (!hdr) { CHECK(0, "cannot read headers"); fclose(f); return; }
    lfanew = rd32(hdr + 0x3c);
    nsec = hdr[lfanew + 6] | hdr[lfanew + 7] << 8;
    so = lfanew + 24 + (hdr[lfanew + 20] | hdr[lfanew + 21] << 8);
    for (i = 0; i < nsec; i++)
        if (memcmp(hdr + so + 40 * i, ".rdata\0\0", 8) == 0) {
            vs = rd32(hdr + so + 40 * i + 8); va = rd32(hdr + so + 40 * i + 12);
            rawsize = rd32(hdr + so + 40 * i + 16); rawptr = rd32(hdr + so + 40 * i + 20);
        }
    CHECK(va == ACZ_RDATA_RVA && vs == ACZ_RDATA_VSIZE, ".rdata of the file: %x/%x", va, vs);
    raw = read_at(f, (long)rawptr, rawsize < vs ? rawsize : vs);       /* .rdata raw bytes: the only other read */
    fclose(f);
    img = (uint8_t *)calloc(ACZ_EXE_SIZE_OF_IMAGE, 1);
    before = (uint8_t *)malloc(ACZ_EXE_SIZE_OF_IMAGE);
    if (!raw || !img || !before || va + vs > ACZ_EXE_SIZE_OF_IMAGE) { CHECK(0, "layout"); return; }
    memcpy(img, hdr, 0x1000);
    memcpy(img + va, raw, rawsize < vs ? rawsize : vs);
    memcpy(before, img, ACZ_EXE_SIZE_OF_IMAGE);

    CHECK(acz_check_header(img, 0x1000, &ex, why, sizeof why), "header: %s", why);
    for (g = 0; g < ACZ_GROUP_COUNT; g++) enabled[g] = probe ? 1 : acz_group_enabled[g];
    CHECK(acz_verify_all(img, &ex, acz_patches, ACZ_PATCH_COUNT, ACZ_GROUP_COUNT, acz_table_sha256, enabled, why, sizeof why), "verify: %s", why);
    for (k = 0; k < ACZ_PATCH_COUNT; k++) expect += enabled[acz_patches[k].group];
    memset(&rc, 0, sizeof rc); rc.base = img;
    CHECK(acz_apply(img, acz_patches, ACZ_PATCH_COUNT, enabled, &ops, &applied, why, sizeof why), "apply: %s", why);
    CHECK(applied == expect, "applied %d, expected %d", applied, expect);
    /* every write request = exactly the object [rva, rva+L+1) of an enabled string */
    CHECK(rc.n == expect, "%d write requests, expected %d", rc.n, expect);
    for (g = 0, k = 0; k < ACZ_PATCH_COUNT; k++) {
        const acz_patch *p = &acz_patches[k];
        if (!enabled[p->group]) continue;
        if (g >= rc.n || rc.off[g] != p->rva || rc.len[g] != p->len + 1u) reqbad++;
        g++;
    }
    CHECK(reqbad == 0, "%d write requests are not [rva, rva+L+1)", reqbad);
    for (j = 0; j < ACZ_EXE_SIZE_OF_IMAGE; j++) {
        int in = 0;
        if (img[j] == before[j]) continue;
        for (k = 0; k < ACZ_PATCH_COUNT; k++)
            if (enabled[acz_patches[k].group] && j >= acz_patches[k].rva && j < (size_t)acz_patches[k].rva + acz_patches[k].len + 1) in = 1;
        if (!in || j < va || j >= va + vs) outside++;
    }
    CHECK(outside == 0, "%zu bytes changed outside [0, L+1) of enabled strings", outside);
    for (k = 0; k < ACZ_PATCH_COUNT; k++) {
        const acz_patch *p = &acz_patches[k];
        const uint8_t *want = enabled[p->group] ? p->new_bytes : before + p->rva;
        CHECK(memcmp(img + p->rva, want, (size_t)p->len + 1) == 0, "string %d (rva %x) content", k, p->rva);
        CHECK(memcmp(img + p->rva + p->len + 1, before + p->rva + p->len + 1, (size_t)p->slot - p->len - 1) == 0, "slot %d (rva %x) tail changed", k, p->rva);
    }
    printf("real image (%s): %d of %d strings written, %zu bytes outside [0, L+1) changed\n",
           probe ? "probe: all groups" : "release groups", applied, ACZ_PATCH_COUNT, outside);
    CHECK(!acz_verify_all(img, &ex, acz_patches, ACZ_PATCH_COUNT, ACZ_GROUP_COUNT, acz_table_sha256, enabled, why, sizeof why) && strstr(why, "already applied"),
          "second run must be refused (%s)", why);

    /* other exe build: any one changed byte of any slot (of any group) breaks the table hash -> nothing is written */
    memcpy(img, before, ACZ_EXE_SIZE_OF_IMAGE);
    if (!probe) {
        for (k = 0; k < ACZ_PATCH_COUNT; k++) {
            const acz_patch *p = &acz_patches[k];
            for (j = 0; j < p->slot; j++) {
                flips++;
                img[p->rva + j] ^= 0x20;
                if (!acz_verify_all(img, &ex, acz_patches, ACZ_PATCH_COUNT, ACZ_GROUP_COUNT, acz_table_sha256, enabled, why, sizeof why) &&
                    strstr(why, "sha256 mismatch")) caught++;
                img[p->rva + j] ^= 0x20;
            }
        }
        CHECK(caught == flips, "one-byte changes caught: %d of %d", caught, flips);
        printf("one-byte change of any slot byte: %d of %d refused by the table hash\n", caught, flips);
        CHECK(acz_verify_all(img, &ex, acz_patches, ACZ_PATCH_COUNT, ACZ_GROUP_COUNT, acz_table_sha256, enabled, why, sizeof why), "image not restored: %s", why);
    }
    free(img); free(before); free(raw); free(hdr);
}

int main(int argc, char **argv)
{
    test_sha256();
    test_synthetic();
    test_table();
    if (argc > 1) { test_real(argv[1], 0); test_real(argv[1], 1); }
    else printf("no acz.exe given: real-image test skipped\n");
    printf("%d passed, %d failed\n%s\n", passed, failed, failed ? "RESULT FAILED" : "RESULT OK");
    return failed ? 1 : 0;
}
